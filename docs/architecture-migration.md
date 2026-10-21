# Native automation core migration

This document is the current architecture and migration record for moving game
automation from the WPF application into `UmamusumeCore.dll`. The WPF project
continues to own presentation, settings editing, emulator discovery and launch,
updates, and resource-version management. The target architecture puts device
sessions, screen capture, recognition, task execution, game tasks, and Career
execution in C++; the status below records the integrated production routes.
`Umamusume.CoreBridge` is the only managed/native boundary and communicates by
the versioned C ABI in `include/UmaAssistant/UmaCaller.h`.

The intended dependency direction is:

```text
WPF -> CoreBridge -> C ABI -> Runtime -> Device / Resources / Vision
                                      -> GameTasks -> TaskEngine
                                                   -> GameData
                                      -> CareerRuntime -> TaskEngine / CareerDomain
```

`CareerDomain` contains game-independent state, observations, action intents,
and strategy rules. It has no dependency on ADB, image libraries, Windows OCR,
WPF, or presentation logging. Career observers report screen evidence;
CareerRuntime coordinates scenario modules, flows, and confirmed actions.
Scenario-specific rules are registered through a scenario interface. The first
scenario remains URA and must preserve the current resource format and decisions.

Each native module is a CMake target with public DTOs in its module boundary.
The DLL target composes those modules. The ABI uses fixed-width values, UTF-8,
opaque handles, caller-owned JSON buffers, and callbacks whose payloads carry a
protocol version, operation ID, task ID when applicable, and monotonically
increasing sequence number. No STL object or native-owned string crosses the
boundary. Bridge DTOs are independent of WPF types; the UI submits immutable
configuration snapshots and displays typed events.

## Compatibility requirements

- Preserve existing task IDs, task settings, databases, image resources, and
  Hachimi JSON version 1, defaults, case-insensitive task names, relative path
  behavior, and supported actions.
- Preserve template matching scores, candidate selection, and coordinates.
  In particular, keep ties-to-even rounding where the current C# uses
  `Math.Round`, as well as reference-size scaling and the fine-scale/local
  refinement passes.
- Keep Windows OCR language selection and fallback behavior. Fixed OCR results
  are used for offline replay; real OCR acceptance is on Windows with MSVC.
- Device work is serialized with the 5-second disconnect monitor. A capture is
  bounded and binary-safe; input must reject frames whose device session or
  geometry generation is stale.
- A handle owns at most one foreground operation. Start failures are synchronous
  and produce no callbacks; accepted operations produce one terminal event.
  Destroy requests cancellation, joins workers, and prevents future callbacks.
- Ordinary task failures continue the queue. Cancellation and device loss stop
  the run and mark remaining work skipped. Native task failures never fall back
  to the legacy executor and repeat an action.
- Resource updates are prepared while running and become visible with cache
  refresh only while idle. Each run observes one configuration and resource
  snapshot.

## Migration status

| Phase | Scope | Status |
|---|---|---|
| 0 | Architecture record, module boundaries, baseline | Architecture and repeatable offline baseline established; original working-tree changes preserved |
| 1 | Device session, binary capture, input, game verification, disconnect handling | Complete; native/fake-device and offline application gates passed |
| 2 | Image decoding, matching, template cache, Windows OCR | Complete; original and affected recognition/image/OCR gates passed |
| 3 | General task engine and daily game tasks | Production integration and offline verification complete within the agreed scope |
| 4 | Career domain, strategies, observers, URA flows and recovery | Integrated; normal and independent Career default to Core. Builds and offline regression passed within the agreed scope. See [Phase 1–4 record](phase4-handoff.md) |
| 5 | Queue ownership, developer tools, legacy cleanup and independent host | Native queue, catalog and host integrated; legacy execution removed. Build/offline acceptance is recorded in [Phase 5 record](phase5-handoff.md). Packaging excluded by request |

Phase 0 has the architecture record, an MSVC Debug preset, and source-owning
`UmaAssistantConnection`, `UmaAssistantDevice`, `UmaAssistantApi`, and
`UmaAssistantCoreRuntime` targets. `UmaAssistantApi` owns the versioned callback
sender, including ordered reentrant delivery and bounded v1/v2 envelope
serialization. These targets compile and link before composition into the DLL.
The Resources, Vision, TaskEngine, GameTasks, CareerRuntime, CareerDomain,
and URA scenario sources now have compiling CMake targets. CareerDomain links
only its pure state and strategy sources; scenario-specific URA strategy and
module sources live under CareerDomain/Scenarios/Ura in a separate target.
GameData owns the local game database and resource paths independently of
device and recognition code. Complete real-game behavior baselines remain
outside this delivery: the user requested builds and offline tests without packaging or real-device acceptance.

Phase 1 has a working native device session and C ABI for package verification,
binary PNG capture, frame leases, transformed tap/swipe, and a five-second
disconnect monitor. The Bridge parses typed events, copies/releases leased
frames, routes device operations, filters disconnects by session generation,
and the WPF connection-health adapter now listens to those native disconnects
instead of probing ADB and reconnecting itself. FakeAdb integration covers the
connect/verify/capture/input path. On 2026-10-10 the native read-only host also
connected to LDPlayer serial `127.0.0.1:16384`, verified the game package, and
captured a 900x1600 PNG of 585,159 bytes (orientation 0); no tap or swipe was
sent to the real device.

Production WPF automation receives IUmaVisualDeviceFacade through DI. Capture,
tap, swipe, held-touch preparation/DOWN/UP/CANCEL, text/key input and package
operations use Core, preserving frame provenance and the existing swipe policy.
The application captures immutable settings and submits the entire queue to Core.
Native Runtime owns scheduling, continuation, cancellation, skip decisions and
the run snapshot. The six ordinary tasks and both Career modes execute through
native GameTasks. The managed Career compatibility route and its executors
have been removed.
Developer screenshots also use
the native facade. IAdbRuntime now exposes only device discovery; obsolete
binary process output, screenshot, tap/swipe, raw-event touch and package
implementations and fallback constructors have been removed. AdbGameLauncher
uses native package operations. The retained AdbRawScreenshot/AdbScreenshotResult
types carry in-memory pixels for OCR/export and do not execute ADB capture.

The current Core ABI is version 11 with 52 C ABI exports. API 9 added pipeline
execution, API 10 added game-task execution and API 11 adds whole-queue
submission, parameter replacement, clearing, run snapshots and catalog queries.
CoreBridge retains earlier capability checks; the production queue needs API 11.
Template
gray, color, button, scaled, and masked-scaled matching, meaningful/persistent
change detection, and Windows OCR are routed through `IUmaService` in production
WPF code. Native recognition has no managed scoring fallback. The 451-case WPF
matcher cohort was rerun through Release Core API 8 after the native resource
cache and preprocessing switch. Its 6,350 native match records were compared
with the previously verified 6,396-record trace. There were zero added or missing distinct inputs,
zero mismatches, and zero maximum score or position error. Repeated identical
inputs can occur a different number of times in polling-based tests. The
comparison is recorded in
`build/phase12-recognition-comparison.json`; the
runner records the native backend and actual Core API version in its trace.
The production `TemplateMatcher` now contains only the native adapter, input
DTO/format conversion and trace/hash support. Its former managed
scoring implementation was removed after this cohort passed.

Phase 2 is switched. GrayImageCodec.FromFile, FromScreenshot and
FromDeviceFrame now decode through CoreBridge RecognitionImageDecoder and
the shared native WIC implementation. Raw RGBA gray conversion also runs in
Core; screenshot provenance is reattached unchanged in the adapter. Image
handles are wrapped in SafeHandle, copied into managed arrays, then released.
ResourceSnapshot uses this same WIC decoder and owns the template pixel cache,
including transparent head and portrait preparation. The cache invalidates
entries when file metadata changes and keeps existing readers' immutable pixels
alive. Managed long-lived template pixel caches have been removed. Crop,
bicubic/nearest resize, mean gray, RGB/HSV evidence, connected color blobs,
numeric glyph isolation, grayscale canvas composition and sampled image
differences now execute in Core. Specialized exhaustive color matching and
colored marker checks use the same native matcher API. C# retains image DTOs,
UI export and editor DTOs. Windows OCR production
calls use the native recognizer. Native Career also owns optional local Tesseract
numeric retries; the managed compatibility path has been removed. The last
numeric-image and specialized matcher changes passed the affected-test gate.

Phase 3 connects the native execution context, Hachimi v1 task graph and
GameTasks composition to the production application. TaskEngine owns generic
recognition, actions, routing, nested pipelines and transition checks;
GameTasks owns startup monitoring, reward collection, shop selection, races
and date-dialog recovery. Game-level selectors are registered by GameTasks,
so the generic action registry does not contain startup or race policy.

The production route is fixed for `start-game`, `mail-collection`,
`mission-collection`, `shop-purchase`, `daily-race` and `team-race`.
`GrassTaskExecutionService` captures every task's settings before execution
and submits one native queue; it only projects ordered events and snapshots
to the UI. Ordinary failures continue; cancellation or device loss skips
the remaining tasks in Core. A native failure does
not invoke a managed task executor. The API uses operation-scoped progress,
typed terminal results and runtime diagnostic events with ordered delivery.

Career is integrated into the default production route. The native runner loads a pinned
manifest, merges resource-defined observers and execution graphs, and composes
entry, normal startup, independent setup, strategies and recovery. Actions
require observed evidence and transition confirmation; normal completion
requires terminal settlement followed by confirmed Home. Generic CareerRuntime
uses registered training flows, while URA visual scanning and scoring live in
separate scenario targets. Goal parsing, race priorities, calendar mapping,
training distance selection and confirmed-result application have parity regressions.
Frame-bound race-result evidence is required before the scenario updates a goal;
unknown, cancelled, stale or duplicate results cannot advance it. Actual Windows OCR
and fixed-frame component replay are tested separately. The current scope,
verification evidence are in [Phase 1–4 record](phase4-handoff.md) and the
[Phase 5 record](phase5-handoff.md).

### Earlier Phase 3 integration verification — 2026-10-11

The isolated Phase 3 sources are now composed into the application route.
`UmaAssistantTaskEngine` owns generic Hachimi v1 execution, while
`UmaAssistantGameTasks` owns the six ordinary task implementations and registers
their business actions. `UmaAssistantGameData` owns database records and resource
paths without device or recognition dependencies. Core Runtime composes the
runner; API 10 exposes `UmaRunGameTaskAsync`; CoreBridge supplies typed progress,
diagnostics and terminal results. The WPF ViewModel delegates scheduling to
`GrassTaskExecutionService`, which uses immutable settings snapshots and a fixed
native route for migrated task IDs. At this earlier checkpoint Career still used its managed route.

Review corrected several draft behaviors: business selector placeholders can no
longer report success without evidence; scaled OCR and portrait coordinates use
the task reference consistently; relative UTF-8 pipeline paths resolve against
their definition directory; missing nested pipelines and unresolved template
collections fail explicitly. A collection still requires an explicit template
until a collection resolver is implemented. Nested execution has bounded depth
and independent pipeline counters, while sub-tasks share their parent counters.

Input checks now bind evidence to the device session, frame geometry, coordinate,
expiry and remaining tap budget. A failed input consumes its attempt; an expired
or exhausted intent cannot be renewed or rebound. Verification retries identify
fresh evidence. Date-dialog recovery distinguishes an action about to run from
an attempted purchase or race submission, preserves confirmed progress, and
restarts only after Home is confirmed. Missing recovery resources or failed
launches return failure without replay. Callback delivery preserves progress,
diagnostic and terminal ordering, and queue subscriptions are scoped to the
current operation.

| Gate | Result | Evidence |
|---|---|---|
| MSVC Debug native suites, C consumer and exact export table | 239/239 passed; 46 exports | `build/phase3-native-tests.log`, `build/phase3-native-build.log` |
| Release Core build | Passed | `build/phase3-release-build.log` |
| Bridge lifecycle, protocol and fresh-process native hosts | 147/147 passed; includes game-task execution, cancellation and 16 OCR fixtures | `build/phase3-bridge-tests.log` |
| Application task routing, queue, settings and recovery | 154/154 passed | `build/phase3-wpf-task-tests.log`, `build/phase3-task-routing-trace.jsonl` |
| Existing device adapter regression | 79/79 passed | `build/phase3-wpf-device-tests.log`, `build/phase3-device-trace.jsonl` |
| Existing recognition cohort through API 10 | 451/451 passed | `build/phase3-wpf-recognition-tests.log`, `build/phase3-recognition-trace.jsonl` |
| Release DLL identity across build, staging and test/application outputs | All eight SHA256 hashes identical | `build/phase3-runtime-hashes.json` |

Native and Bridge builds passed with no warnings or errors. The WPF build passed
with the existing ImageSharp NuGet audit advisories; dependency upgrades were
outside this migration. Managed tests used the direct runners because VSTesthost
still fails with Windows error 5 on this machine. All reported suites have zero
failures and zero skipped cases. `git diff --check` passed.

Validation used FakeAdb, fixed OCR outputs, packaged resources and synthetic
screens. The integration host exercised the actual DLL and checked ordered input
commands, failure results and cancellation. No real-game input or packaging was
performed in this Phase 3 pass. Each ordinary task's real-game closed-loop
acceptance from the migration plan remains pending; the offline results do not
claim that gate or Phase 4 completion.

### Device and recognition acceptance

Source-frame identity, crop/resize coordinate transforms, bounded frame leases,
consumption/expiry, disconnect/geometry invalidation, date-dialog interruption
and held-touch pinning/preflight are implemented in the current production
facade and covered by offline/fake-device tests. Retained flashing-button retry
intents explicitly bind coordinates, source generations, lifetime and tap budget.
Their leases are separate from ordinary analysis frames and cannot be silently
rebound to a new capture. Held-touch cancellation uses the non-cancelable cleanup
path and must be idempotent during disconnect/shutdown.

The device migration and managed screenshot/input cleanup have passed the
offline application gate (79/79), final native tests (214/214), and Bridge
tests (131/131). The original 451-case matcher cohort
has zero score/coordinate/threshold differences. Prepared real WebP templates
and synthetic resize cases also passed exact pixel comparisons. The final
numeric-image additions passed their separate affected-test gate (158/158,
including 17 exact-pixel checks). These gates do not claim real game task or complete
Career execution, which belongs to Phases 3 and 4.

The tracked runner at `tools/RunTemplateMatcherGolden.ps1` always initializes
the production native Core backend; `-Native` is accepted for command
compatibility and does not select another implementation. It records each
outcome and writes content-addressed gray/RGBA input sidecars under ignored
`build/`. Masked matching and masked alpha preprocessing have a
183-outcome/95-match review trace with pixel sidecars. That sample is synthetic
and uses the packaged button-head image; it is not a real character-selection
screen replay.

### Phase 1 and 2 final verification — 2026-10-11

The device and recognition phases are complete under their stated switch
conditions. Production capture/input/package calls and Windows OCR/matching
use the native implementations. Emulator discovery, configuration, UI export,
game-level orchestration and the later migration phases retain their assigned
managed responsibilities.

| Gate | Result | Evidence |
|---|---|---|
| MSVC Debug native suites, C consumer and exact export table | 214/214 passed; 43 exports | `build/phase12-final-native-tests.log`, `build/phase12-final-native-build.log` |
| Bridge lifecycle, layout, reload/disposal races and isolated native device host | 131/131 passed | `build/phase12-final-bridge-tests.log` |
| Application device/discovery/provenance adapters | 79/79 passed | `build/phase1-offline-device-trace.jsonl` |
| Original recognition choices, coordinates, scores and thresholds | 451/451 passed; zero distinct-input differences | `build/phase12-recognition.jsonl`, `build/phase12-recognition-comparison.json` |
| Affected specialized recognition, color, glyph and image adapters | 158/158 passed, including 17 exact-pixel checks | `build/phase2-image-migration-release-trace.jsonl` |
| Energy-bar ROI-copy optimization | 5/5 passed after the final source change | `build/phase2-energybar-roi-trace.jsonl` |
| Fixed Windows OCR text, line bounds and language fallback | 16/16 passed in the same Windows environment | `build/phase12-final-ocr-golden.log` |
| Self-contained Release package and resource layout | 3/3 passed | `build/phase12-final-package.log`, `build/phase12-final-package.jsonl` |
| Release DLL identity across build, staging, GUI, tests and publish | All five SHA256 hashes identical | `build/phase12-final-runtime-hashes.json` |

Release native, Bridge and WPF builds passed with zero warnings/errors.
`git diff --check` also passed. Tests used offline images, fixed OCR outputs
and FakeAdb; no real game input was sent. The package verification was run
with process-local `NuGetAudit=false` to bypass the existing ImageSharp audit
failure recorded above; project auditing settings were not changed.

VSTesthost cannot open its parent process on this machine (Windows error 5).
Managed suites therefore used the existing direct xUnit runners, retaining
normal discovery, assertions and reported pass/fail/skip counts. The final
phase results have zero failures and zero skipped cases. These gates complete
Phases 1 and 2 and do not assert completion of Phases 3–5 or real-device daily
task/Career acceptance.

Nothing is considered migrated merely because a target or ABI declaration
exists. A behavior is marked switched only after its native implementation is
called through the production application path and its regression tests pass.

## Baseline and validation

The baseline revision at migration start was
`52619a03d008ffa98e35f0529c34af5583440fa7` (working tree clean). Baseline test
results and current verification are recorded separately so environmental
failures and pre-existing resource expectations do not become migration
regressions.

- Initial `dotnet test` attempts for Bridge and WPF compiled but the VSTest
  host failed before discovery with `Win32Exception (5): Access is denied`
  from `SetParentProcessExitCallback`: 0 discovered, 0 passed, 0 failed, and
  0 skipped. Use the checked-in-project outputs with the direct xUnit runners
  under ignored `build/bridge-test-runner` and `build/ratio-test-runner`; set
  `TEMP` and `TMP` to the writable `build/msvc-debug/test-temp` first. The
  default AppData Local Temp path is denied in this environment. WPF restore
  also reports the existing ImageSharp 3.1.11 NU1902/NU1903 advisories;
  `-p:NuGetAudit=false` is used only for one-off builds and is not committed.
- The WPF `HachimiPipelineDefinitionTests` baseline was 24 total, 23 passed,
  1 failed, 0 skipped. The existing test expects `MatchTemplate` while the
  shipped support-picker resource is `MatchTemplateColor`; preserve the
  resource's actual color behavior. The original 1,580-case WPF run hit the
  local direct-runner's three-minute limit and is incomplete, so it has no
  pass/fail total.
- `cmake --preset msvc-debug` configured successfully with VS 17.14 / MSVC
  19.44. The initial CTest inventory contained 8 cases; native test binaries
  had not yet been built or executed at baseline. The earlier Phase 2 inventory
  contained 212 cases: 210 native tests plus the `ManagedBridgeTests` and
  `WpfGuiTests` managed test entry points.

Historical Windows verification on 2026-10-10:

- MSVC Debug CTest passed 182/182 in 165.52 seconds; see
  `build/supervisor-final-native-verified.log`. The earlier full run had one
  cancellation-test readiness failure because the fixed 500 ms cancel could
  arrive before the fake child PID file was written. The test now waits for a
  readable nonzero PID with a bound, requests cancellation on timeout, and
  cleans up any remaining child. The rebuilt full suite passed; no behavior
  assertion was weakened.
- CoreBridge direct xUnit passed 118/118 with `UMA_NATIVE_INTEGRATION=1`, zero
  skipped; see `build/supervisor-final-bridge.log`. The integration host passed
  all six base scenarios, including FakeAdb device controls and connection, and
  validated 16 OCR golden records across the main and language-edge corpora.
  Its direct scenario output is in `build/supervisor-final-bridge-host-scenarios.log`.
- The native-only WPF matcher cohort passed 451/451, zero skipped, using the
  Release Core API 3 DLL before the additive held-touch preflight export; trace and complete runner output are
  `build/supervisor-native-recognition-final-verified.jsonl` and
  `build/supervisor-native-recognition-final-verified.log`. The supervising
  comparison against the historical matcher corpus is in
  `build/supervisor-native-recognition-final-verified-comparison.json`:
  6,414 records compared, zero added, missing distinct inputs, or mismatches,
  and zero maximum score or position error.
- WPF tests now copy the production-excluded URA capture and testdata fixtures
  into test output only (`CopyToPublishDirectory=Never`). This fixes the
  resource-root ambiguity caused by a partial staged `resource` directory.
  A first final-cohort attempt stopped after missing captures; its partial trace
  and status are preserved as `build/supervisor-native-recognition-missing-fixtures-incomplete.jsonl`
  and `.status.txt`. The next complete run exposed one false-branch Career
  resume test deadline failure. The test now distinguishes fixture-triggered
  cancellation from its safety watchdog, keeps its tap/cache/scope assertions,
  and uses a two-minute watchdog. Both branches passed an isolated run in
  38.21 seconds total; the full 451-case rerun above passed as well.
- Core API 3 runtime copies at recognition verification in native staging, publish output, and WPF test
  output have identical SHA256 hashes; details are in
  `build/supervisor-final-runtime-hashes.json`.
- WPF regression smoke passed 208/208 with zero failures or skips, including
  package layout and application bootstrap tests. Its trace is
  `build/supervisor-native-recognition-smoke-final.jsonl`. Later changes only
  adjusted test fixtures and the Career resume test watchdog; the final matcher
  cohort was rerun after those changes. WPF checks use the test bootstrap's real
  local Core DLL and do not connect to an emulator or send game input.
- The recorded 1,770-line cleanup comprises 1,397 lines of obsolete managed
  matcher scoring code and 373 lines of unused C# ADB interface/runtime
  methods and helpers. Active connection, launch, package, key, and text
  consumers remained at that verification point. Image decoding and
  masked-template preprocessing were still managed then; the decoder was
  subsequently switched in the 2026-10-11 continuation below.

Current Windows verification on 2026-10-11:

- MSVC Debug CTest passed 210/210 in 156.91 seconds, excluding the two managed
  VSTest entry points; see `build/continuation-native-tests.log`. This includes
  the newly compiled TaskEngine and Career suites, capture timeout, recursion
  bounds, action/confirmation failures, cancellation and case-insensitive
  execution limits. Draft test success does not constitute production migration.
- CoreBridge direct xUnit passed 130/130 with native integration enabled and
  zero skipped; see `build/continuation-bridge-tests.log`. The C consumer image
  ABI/buffer regression also passed after its portable file-open adjustment;
  see `build/continuation-c-consumer-tests.log`.
- The WPF native matcher cohort passed 451/451 with zero skipped on Core API 7;
  see `build/continuation-native-recognition.jsonl` and `.log`. The comparison
  above confirms exact score and coordinate parity for all distinct inputs.
- Focused WPF codec/device/provenance/bootstrap tests passed 54/54, zero skipped;
  see `build/continuation-wpf-focused-final.log`. Encoded PNG, BMP, JPEG, TIFF,
  GIF, indexed PNG with transparency, a shipped WebP, and URA captures produce
  exactly the legacy WPF gray and RGBA planes. Raw RGBA conversion and frame
  provenance are covered separately.
- WPF smoke passed 257/258 with zero skipped; see
  `build/continuation-wpf-smoke.log` and `.jsonl`. The single package-script
  failure occurred during restore because the existing ImageSharp 3.1.11
  NU1902/NU1903 advisories are treated as errors. Its isolated packaging rerun
  passed 1/1 with process-local `NuGetAudit=false`; see
  `build/continuation-package-recheck.log` and `.jsonl`. The original failure is
  retained and auditing is not disabled in project files. An earlier isolated
  rerun stalled in the process-tree wait after successful publish and was
  stopped; its output is in `build/continuation-package-wait-incomplete.log`.
  `tools/package.ps1` now invokes install/publish directly and checks their exit
  codes, avoiding that wait. The passing ZIP test verifies native DLLs,
  self-contained runtime, resource layout, and exclusion of test fixtures.
- Debug and Release verify all 33 undecorated C exports. The Release build,
  staging, GUI, WPF-test and publish runtime copies have identical SHA256 values;
  see `build/continuation-runtime-hashes.json`. Debug native and Release WPF
  builds pass with strict warnings enabled.
- Cleanup removed the unused managed package launch/activity/stop/query methods
  and their private parsing helpers, plus the replaced managed image decoder.
  Developer screenshot and legacy test-constructor consumers still need the
  remaining managed capture/input code. Unreferenced root diagnostic artifacts
  were moved into ignored `build/continuation-leftover-diagnostics` so they are
  recoverable. Existing uncommitted migration work was retained.
- This continuation used offline images/OCR and FakeAdb only. No game input
  was sent to a real emulator or device.

`cmake --preset msvc-debug`, `cmake --build --preset msvc-debug`, and
`ctest --preset msvc-debug -E 'ManagedBridgeTests|WpfGuiTests'` are the required local MSVC Debug path. The
`release` preset targets Visual Studio 2022 Release. Native tests require
`TEMP` and `TMP` to point to a writable directory such as
`build/msvc-debug/test-temp` on this machine.

Use fake ADB and fixed image/OCR fixtures for deterministic tests. The .NET suites
use the tracked `tools/RunCoreBridgeTests.ps1` and `tools/RunTemplateMatcherGolden.ps1`
direct runners on this machine. Current Phase 1–4 offline filters are in
`tools/phase1-4-offline-test-filters.json`. Packaging and real-device tests are
outside the current user-requested scope. Earlier verification sections above
are historical snapshots, with their original ABI versions and test counts;
the latest gates are recorded in [Phase 1–4 record](phase4-handoff.md).
