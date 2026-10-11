# Native template matcher replay fixtures

Each fixture folder contains `manifest.json` and compressed `UIMG v1` inputs
captured from the C# matcher at runtime. The manifest records the original
screen/template paths, the source revision and matcher source blob, the exact
call parameters, and the observed C# result. A replay should compare score to
`1e-6`, coordinates to one pixel, and preserve candidate selection and
threshold behavior.

`independent-add-skills` is a positive real-screen regression. Its input pixels
were captured from the existing PNG fixture and built-in template, then
checked by decompressing both payloads, validating lengths and EOF, and
recomputing each SHA-256 using the same width/height/gray/RGBA byte order as
`TemplateMatcher.HashImage`.
