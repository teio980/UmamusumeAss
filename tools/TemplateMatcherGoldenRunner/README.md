# Template matcher golden capture

Use `tools/RunTemplateMatcherGolden.ps1` to build and capture the selected
matcher class. It defaults to `DeveloperToolsImageMatchTests`; pass class-name
filters to choose additional coverage, for example:

```powershell
.\tools\RunTemplateMatcherGolden.ps1 -Filter DeveloperToolsImageMatchTests
```

The runner always initializes the staged `UmamusumeCore.dll` and routes
`TemplateMatcher` calls through the production Bridge/C ABI. `-Native` remains
accepted for command compatibility and does not select a separate backend.
Each trace records `native: true`, `backend: "UmaAssistantCore"`, and the
actual `coreApiVersion`; input sidecars and matcher parameters remain stable
for comparison with the native corpus.

The script preserves NuGet audit by default. On this machine only, use the
explicit `-IgnoreNuGetAudit` switch when the existing ImageSharp advisory blocks
an otherwise unrelated local build.

The runner writes one JSONL file with run metadata, each discovered test case,
each passed/failed/skipped outcome, and each matcher result. It flushes every
record. Screen and in-memory template inputs are stored once by content hash as
compressed `.uimg.gz` files beside the trace. Each payload contains the `UIMG`
format marker/version, little-endian width and height, gray-byte count, RGBA
byte count (`-1` when absent), then gray and optional RGBA bytes before gzip
compression. A masked template keeps its resource-relative source path in the
trace. These artifacts live under `build/` and are reproducible from the
tracked runner source and command above.

The 2026-10-10 initial matcher corpus run completed 450/450 cases successfully,
but its temporary trace did not contain pixels and its last line was truncated.
That trace is not a replayable baseline. The tracked runner is used for the
selected input captures and future complete baselines; current capture coverage
must be listed by class and method rather than inferred from the old corpus
count.
