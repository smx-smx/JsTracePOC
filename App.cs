/**
 * JsTraceCalls: an electron script to trace code execution inside JS files
 * Copyright(C) 2018 Stefano Moioli <smxdev4@gmail.com>
 * 
 **/
using Bridge;
using Newtonsoft.Json;
using Retyped;
using System;
using static Retyped.electron.Electron;
using util = Retyped.node.util;
using lit = Retyped.electron.Literals;
using mt = System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static Retyped.electron.NodeJS;
using System.Threading.Tasks;

namespace JsTraceCalls
{
    public class ScriptInfo
    {
        public string IDString;
        public string Location;
        public string Name;
        public string[] Lines;
        public bool IsTracked = false;
        public bool IsHooked = false;
    }

    public class App
    {
        [Init(InitPosition.Top)]
        public static void InitGlobals()
        {
            node.require.Self("./bridge.js");

            // The call below is required to initialize a global var 'Electron'.
            var _Electron = (AllElectron)node.require.Self("electron");

            // Keep a global reference of the window object, if you don't, the window will
            // be closed automatically when the      object is garbage collected.
            BrowserWindow _MainWindow = null;
        }

        [Template("_Electron")]
        public static AllElectron Electron;

        [Template("_MainWindow")]
        public static BrowserWindow MainWindow;


        private Dictionary<int, ScriptInfo> scripts = new Dictionary<int, ScriptInfo>();
        private Debugger dbg;

        private bool ProcessingEnabled = true;

        private void StopProcessing()
        {
            ProcessingEnabled = false;
        }

        private void StartProcessing()
        {
            ProcessingEnabled = true;
        }

        private void Pause()
        {
            dbg.sendCommand("Debugger.pause");
        }

        private void Resume()
        {
            dbg.sendCommand("Debugger.resume");
        }
        
        public App(string url, string bpRegex)
        {
			PAGE_URL = url;
			BP_REGEX = bpRegex;
        }

        private void AfterInit()
        {
            Console.WriteLine("Loading complete");
            MainWindow.focus();
        }

        private string PAGE_URL = null;
        private const bool USE_SPIDER = true;
        private string BP_REGEX = ".*";

        private Task<object> AsyncCommand(string command, object para)
        {
            var tcs = new TaskCompletionSource<object>();
            dbg.sendCommand(command, para, (err, res) => {
                if(object.GetOwnPropertyNames(err).Length != 0) {
                    dynamic data = err.ToDynamic();
                    if (command == "Debugger.setBreakpoint" && data.code == -32000) {
                        // Breakpoint already exists, ignore...
                    } else {
                        throw new Exception(util.inspect.Self(err));
                    }
                }
                tcs.SetResult(res);
            });

            return tcs.Task;
        }

        private async Task<object> PlaceBreakpoint(object para)
        {
            return await AsyncCommand("Debugger.setBreakpoint", para);
        }

        private async void HookScript(int id, ScriptInfo info)
        {
            StopProcessing();
            Pause();

            for(int i=1; i<=info.Lines.Length; i++) {
                await PlaceBreakpoint(new {
                    location = new {
                        scriptId = info.IDString,
                        lineNumber = i
                    }
                });
                // Disable for logging
                Progress(i, info.Lines.Length);
            }
            info.IsTracked = true;
            info.IsHooked = true;

            StartProcessing();
            Resume();
        }

        private void OnScriptParsed(dynamic data)
        {
            string location = data.url;
            int scriptId = int.Parse(data.scriptId);
            Console.WriteLine($"=> Loaded {location} (ID: {scriptId})");


            dbg.sendCommand("Debugger.getScriptSource", new {
                scriptId = data.scriptId
            }, (err, msg) => {
                string src = msg.ToDynamic().scriptSource;

                ScriptInfo info = new ScriptInfo() {
                    Location = location,
                    Name = node.path.basename(location),
                    Lines = src.Split('\n'),
                    IDString = data.scriptId
                };

                scripts[scriptId] = info;

                if (
                    (Regex.IsMatch(location, BP_REGEX) || info.Name.Length < 1) && !info.IsHooked
                ) {
                    Console.WriteLine($"!! HOOKING {info.Name} ({info.Lines.Length} breakpoints)!!");
                    HookScript(scriptId, info);
                    Console.WriteLine("Done");
                }
            });
        }

        private string[] spinner = {"/", "-", "\\", "|"};
        private uint spinnerCounter = 0;
        private uint spinnerStep = 10;

        private void Progress(int counter, int total)
        {
            var stdout = node.process2.stdout.ToDynamic();
            node.readline.clearLine(stdout, 0);
            node.readline.cursorTo(stdout, 0);
            node.process2.stdout.write($"{spinner[((spinnerCounter++)/spinnerStep) % spinner.Length]} {counter}/{total}");
            //node.process2.stdout.write($"{counter}/{total}");

            if (spinnerCounter >= spinnerStep * spinner.Length) {
                spinnerCounter = 0;
            }
        }

        private void Debug()
        {
            dbg = MainWindow.webContents.debugger;
            if (!dbg.isAttached()) {
                dbg.attach();
            }

            dbg.on(lit.message, (ev, message, para) => {
                //Console.WriteLine(message);

                if (!ProcessingEnabled)
                    return;

                switch (message) {
                    case "Debugger.breakpointResolved":
                        break;
                    case "Debugger.scriptParsed":
                        dynamic data = para.ToDynamic();
                        OnScriptParsed(data);
                        break;
                    case "Debugger.paused":
                        //Console.Write("enter:");
                        object[] frames = para.ToDynamic().callFrames;
                        dynamic frame = frames[0];

                        int scriptId = int.Parse(frame.location.scriptId);
                        if (scripts.ContainsKey(scriptId)) {
                            // print line of script
                            int lineNo = int.Parse(frame.location.lineNumber);

                            ScriptInfo info = scripts[scriptId];
                            if (info.IsTracked) {
                                Console.WriteLine($"[{info.Location}:{lineNo}] => {info.Lines[lineNo]}");
                                //dbg.sendCommand("Debugger.stepInto");
                            }
                            dbg.sendCommand("Debugger.resume");
                        }
                        //Progress();
                        break;
                    case "Debugger.resumed":
                        //Console.Write("leave:");
                        break;
                    default:
                        Console.WriteLine($"Unhandled message '{message}'");
                        break;
                }
            });

            dbg.sendCommand("Debugger.enable");

            dbg.sendCommand("Debugger.setSkipAllPauses", new {
                skip = false
            }, (err, result) => {
                //node.console2.log(err);
            });

            dbg.sendCommand("Debugger.setBreakpointByUrl", new {
                lineNumber = 1,
                urlRegex = BP_REGEX
            }, (err, result) => {
                node.console2.log(result);
            });

            dbg.sendCommand("Debugger.setBreakpointsActive", new {
                active = true
            }, (err, result) => {
                //node.console2.log(err);
            });

            bool stop = false;
            MainWindow.webContents.on(lit.did_finish_load, () => {
                stop = true;
                AfterInit();
            });
        }

        public void Ready()
        {
            // Write a message to the Console
            Console.WriteLine("Welcome to Bridge.NET");

            WebPreferences wp = new WebPreferences()
            {
                sandbox = true,
                contextIsolation = true,
                nodeIntegration = false
            };
            if (USE_SPIDER)
            {
                wp.preload = @"bin\Debug\bridge\spider.js";
            }

            MainWindow = new BrowserWindow(new BrowserWindowConstructorOptions() {
                width = 1280,
                height = 600,
                title = "JsTracer",
                skipTaskbar = false,
                webPreferences = wp
            });

            MainWindow.webContents.setUserAgent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/66.0.3359.139 Safari/537.36");

            //$NOTE: don't enable this, or it will detach debugger from our end
            /*if (!MainWindow.webContents.isDevToolsOpened()) {
                MainWindow.webContents.openDevTools();
            }*/

            MainWindow.webContents.once(lit.did_start_loading, () => {
                //$TODO: Do we need to clear scripts/breakpoints when changing page?
                scripts.Clear();
                Debug();
            });

            MainWindow.loadURL(PAGE_URL);

            MainWindow.webContents.on(lit.crashed, (ev, input) => {
                throw new Exception(ev.ToString());
            });
        }

        public void Entry()
        {
            var app = Electron.app;

            app.on(lit.window_all_closed, () => {
                if (node.process2.platform != "darwin") {
                    app.quit();
                }
            });

            app.on(lit.ready, () => Ready());
        }

        public static void Main()
        {
			string url = "http://www.google.com";
			string bpRegex = ".*";

            App myApp = new App(url, bpRegex);
            myApp.Entry();
        }
    }
}