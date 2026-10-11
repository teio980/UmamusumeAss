# Core host and offline replay

This .NET 10 command-line host references only `Umamusume.CoreBridge`. Core
owns devices, recognition, the task queue, daily tasks and both Career modes.
The host submits configuration and prints structured results. Build and tests
do not require WPF or an installer.

```powershell
cmake --preset msvc-debug
cmake --build --preset msvc-debug
dotnet build tools/Umamusume.CoreBridge.IntegrationHost -c Debug `
  -p:NativeDllPath="$PWD/build/msvc-debug/src/UmaAssistantCore/Debug/UmamusumeCore.dll" `
  -p:FakeAdbPath="$PWD/build/msvc-debug/tests/Connection/Debug/UmaFakeAdb.exe"

$hostDll = 'tools/Umamusume.CoreBridge.IntegrationHost/bin/Debug/net10.0/Umamusume.CoreBridge.IntegrationHost.dll'
dotnet $hostDll catalog all
dotnet $hostDll validate-pipeline resource/hachimi/pipelines/mail_collection.json
dotnet $hostDll queue-run 'C:/path/to/adb.exe' '127.0.0.1:5555' queue.json
```

`queue-run` accepts a queue file, an optional resource base directory (containing
`resource/`), and an optional persistent user directory. The default user
directory is `%LOCALAPPDATA%/UmamusumeAss/CoreHost`, preserving Career
checkpoints. Relative resource references follow the native resource snapshot;
use absolute `definitionPath` values for custom definitions. Ctrl+C cancels
the native run and awaits its terminal result. Progress and the final snapshot
go to stderr; stdout contains the final JSON result. Exit codes are 0 for
all tasks succeeding, 1 for any task or queue failure and 2 for cancellation.
`result.Succeeded` means the queue reached its normal terminal state; ordinary
task failures can still appear in `result.FailedTasks`.

Example queue configuration retains the existing task IDs and settings:

```json
{
  "tasks": [
    { "id": "mail-collection", "settings": {} },
    { "id": "mission-collection", "settings": {} }
  ]
}
```

An ordinary failure continues to the next task. Cancellation and device loss
stop the queue and mark remaining tasks skipped. Each entry has its own native
task instance ID, including duplicate task names. `career-run <adb> <serial>
<settings.json>` remains available for the existing single-task integration.

## Offline evidence replay

The native `uma_pipeline_replay` executable uses the same Hachimi v1 loader,
graph runner and action registry targets as Core. Its context consumes recorded
recognition evidence and records proposed inputs without opening a device.
This verifies flow orchestration; template scores and OCR in the fixture are
frozen evidence, not fresh image recognition.

```powershell
& build/msvc-debug/tools/Debug/uma_pipeline_replay.exe `
  tools/replay-fixtures/transition.pipeline.json `
  tools/replay-fixtures/transition.replay.json
& tools/TestPipelineReplay.ps1
& tools/TestHeadlessCommands.ps1
& tools/RunPhase5Tests.ps1
```

The Phase 5 script excludes `Live` and `Packaging` test traits. It builds and
runs offline checks without publishing, creating an archive or an installer.

The CLI accepts `<pipeline.json> <replay.json> [entry-task]`. The replay schema
is version 1 with a nonempty `frames` array. Each capture consumes the next
frame. `matches` keys use the template strings in the pipeline; boxes are in
the task's reference coordinates. Optional `ocr` entries contain text, x, y,
width, height and confidence. Optional frame flags are `captureSucceeded`,
`actionSucceeded`, `cancelled`, `imageChanged`, and `hsvMatched`. Missing
recognition evidence remains unknown or unmatched. Defaults never invent a
matched button or OCR result.

```json
{
  "version": 1,
  "operationBudget": 10000,
  "frames": [
    { "matches": { "button.png": { "score": 0.99, "x": 10, "y": 20, "width": 30, "height": 40 } } },
    { "ocr": [{ "text": "Done", "x": 10, "y": 20, "width": 30, "height": 40, "confidence": 1 }] }
  ]
}
```

The result includes success, status, final task, task counts, capture count,
recorded actions, graph events and logs. Exhausted evidence and exceeded
operation budgets fail explicitly. Waits consume a recorded duration and do
not sleep; native flow deadlines remain those of the shared engine. Inputs are
bounded to 16 MiB, 10,000 frames and 100,000 adapter operations. Selector
actions that require task-specific business settings remain explicitly unbound
when replaying a generic pipeline, matching the generic Core pipeline route.

## Career package editor inspection

The same executable exposes the production Career package parser. Inspection
reads a manifest and returns its source files, merged execution definition,
task order and diagnostics. It never writes the package or opens a device.
The editor supplies every current JSON buffer as an overlay, so validation
includes changes that have not been saved.

```powershell
& build/msvc-debug/tools/Debug/uma_pipeline_replay.exe `
  --inspect-career-package "$PWD/resource/hachimi/ura/manifest.json" request.json
& tools/TestCareerPackageInspection.ps1
```

`request.json` is optional and contains `resource_root` (the absolute top-level
resource directory) and `files`, an array of `{ "path": "absolute JSON path",
"raw_json": "current editor text" }`. Overrides must remain within that resource
directory; duplicate paths and malformed JSON are reported. Exit code 0 means
valid and 1 means invalid. Invalid results retain source text and diagnostics
for the editor. Production readiness and strict editor validity are separate:
a package with an unusable action binding can be partially ready while invalid
for saving. `execution.definition`, `task_order`, `fragment_paths` and
`base_directory` come from the production parser rather than a managed merger.
