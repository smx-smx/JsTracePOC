# JsTracePOC
Proof of concept that traces all Javascript lines by putting a breakpoint on each line.

Not sure how well it works with minified files since they pack more lines into one.

## Note: It's not particularly fast/efficient to place a breakpoint per line.

An alternative could be to replace all JS statements with a logger trampoline, but it would alter the original script
