# Emulator Linking Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make Windows emulator linking reliable across MuMu process discovery, ADB readiness races, transient offline states, cancellation, auto-start, profile selection, and post-connect health.

**Architecture:** Keep the current WPF → managed bridge → C++ connection boundary. The WPF layer discovers emulator entry points and performs cancellable candidate probing; the native connector remains the authoritative handshake and owns bounded retry. A managed health monitor observes the verified connection and requests a serialized native reconnect when the transport disappears.

**Tech Stack:** C#/.NET 10 WPF, Stylet IoC, xUnit, C++20, CMake, Catch2, Win32 `adb.exe` process execution.

## Global Constraints

- Implement the approved scope in `docs/superpowers/specs/2026-07-29-emulator-linking-reliability-design.md`.
- Do not implement MAA screencap benchmarking, RawByNc, minitouch/maatouch, vendor DLL extras, coordinate scaling, or game tasks.
- Retry defaults are `max_attempts = 3`, `retry_interval = 2s`; keep existing per-command and ready-poll deadlines.
- Retry only transport/startup failures; never retry invalid arguments, missing ADB paths, unauthorized devices, malformed identity, malformed geometry, or cancellation.
- Use `IAsyncDelay.DelayAsync(TimeSpan, CancellationToken)` for managed polling and tests; no real test sleeps.
- Preserve the existing C ABI callback ordering and never use type-error suppressions.
- Do not create commits; the user requested implementation, not git history changes.

## File Map

| File | Responsibility |
|---|---|
| `src/UmamusumeWpfGui/Helper/EmulatorProfileCatalog.cs` | Recognized emulator process aliases and finite endpoint/path catalog. |
| `src/UmamusumeWpfGui/Models/ConnectionSettings.cs` | Accept known connection profile names without silent corruption. |
| `resource/connection.json` | General ADB command set plus MAA-style inherited named profiles. |
| `src/UmaAssistantCore/Connection/EmulatorConnector.hpp` | Native retry policy and attempt metadata. |
| `include/UmaAssistant/Connection.hpp` | Native failure attempt fields with defaults. |
| `src/UmaAssistantCore/Connection/EmulatorConnector.cpp` | Retryable handshake loop. |
| `src/UmaAssistantCore/Connection/EmulatorConnector_ResolveTarget.cpp` | Offline recovery and endpoint ready polling. |
| `src/UmaAssistantCore/CoreHandle.cpp` | Expose attempt metadata in failure callbacks. |
| `src/UmamusumeWpfGui/Helper/IAdbRunner.cs` | Cancellable async ADB command contract. |
| `src/UmamusumeWpfGui/Helper/AdbRunner.cs` | Async bounded process execution with cancellation/kill. |
| `src/UmamusumeWpfGui/Helper/EndpointResolver.cs` | Async endpoint discovery, connect, poll, retry, diagnostics. |
| `src/UmamusumeWpfGui/Helper/IWinAdapter.cs` / `WinAdapter.cs` | Async resolver boundary and injected delay. |
| `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.cs` / `.AutoStart.cs` | Token propagation and launch → rediscovery → connect flow. |
| `src/UmamusumeWpfGui/Services/ConnectionHealthMonitor.cs` | Serialized post-connect probe and reconnect lifecycle. |
| `src/UmamusumeWpfGui/Bootstrapper.cs` | Health monitor registration. |
| `tests/UmamusumeWpfGui.Tests/...` | Managed regression coverage. |
| `tests/Connection/EmulatorConnectorTests.cpp` | Native retry/attempt coverage. |
| `tests/Connection/ConnectionProfileTests.cpp` | Named inherited profile coverage. |
| `tests/Connection/FakeAdb.cpp` / `UmaCallerTests.cpp` | Offline-then-ready integration scenario. |

---

### Task 1: Lock emulator aliases and profile contracts

**Files:**
- Test: `tests/UmamusumeWpfGui.Tests/Helper/EmulatorProfileCatalogTests.cs`
- Modify: `src/UmamusumeWpfGui/Helper/EmulatorProfileCatalog.cs`
- Test: `tests/UmamusumeWpfGui.Tests/Models/ConnectionSettingsTests.cs`
- Modify: `src/UmamusumeWpfGui/Models/ConnectionSettings.cs`
- Test: `tests/Connection/ConnectionProfileTests.cpp`
- Modify: `resource/connection.json`

**Interfaces:**
- `EmulatorProfileCatalog.TryGetForProcess("MuMuNxMain", out profile)` returns a `MuMuEmulator12` profile.
- `ConnectionSettings.ConnectConfig` preserves exactly `General`, `MuMuEmulator12`, `LDPlayer`, `BlueStacks`, `Nox`, `XYAZ`, `WSA`, and `Androws`; unknown/empty values become `General`.
- All named JSON profiles inherit the General command set.

- [ ] **Step 1: Add failing managed alias/profile tests.**

```csharp
[Theory]
[InlineData("MuMuNxMain")]
[InlineData("MuMuPlayer")]
[InlineData("MuMuNxDevice")]
public void MuMuProcessAliases_MapToMuMuProfile(string processName)
{
    Assert.True(EmulatorProfileCatalog.TryGetForProcess(processName, out var profile));
    Assert.Equal("MuMuEmulator12", profile.Name);
}

[Theory]
[InlineData("MuMuEmulator12")]
[InlineData("LDPlayer")]
[InlineData("BlueStacks")]
[InlineData("Nox")]
[InlineData("XYAZ")]
[InlineData("WSA")]
[InlineData("Androws")]
public void ConnectConfig_PreservesKnownProfile(string profile)
{
    var settings = new ConnectionSettings { ConnectConfig = profile };
    Assert.Equal(profile, settings.ConnectConfig);
}
```

- [ ] **Step 2: Run the focused managed tests and verify the alias/profile assertions fail.**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~EmulatorProfileCatalogTests|FullyQualifiedName~ConnectionSettingsTests"
```

Expected: the new MuMu alias assertion fails, and known non-General profiles are rewritten to `General`.

- [ ] **Step 3: Add `MuMuNxMain` and a single known-profile set.**

Keep the profile set case-sensitive for persisted profile names and keep process-name matching case-insensitive. Make the setter null-safe without adding a type suppression.

- [ ] **Step 4: Add the seven inherited JSON profiles and native inheritance tests.**

Each entry has `baseConfig: "General"` and `commands: {}`. Test `expand(profile, "get_size", adb, serial)` for every profile and assert the exact inherited arguments.

- [ ] **Step 5: Run focused managed and native profile tests.**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~EmulatorProfileCatalogTests|FullyQualifiedName~ConnectionSettingsTests"
ctest --preset release -R ConnectionProfileTests --output-on-failure
```

Expected: all focused tests pass.

---

### Task 2: Add native retry policy and failure attempt metadata

**Files:**
- Test: `tests/Connection/EmulatorConnectorTests.cpp`
- Modify: `src/UmaAssistantCore/Connection/EmulatorConnector.hpp`
- Modify: `include/UmaAssistant/Connection.hpp`
- Test: `tests/Umamusume.CoreBridge.Tests/CallbackParserTests.cs`
- Modify: `src/Umamusume.CoreBridge/Protocol/ConnectionEvents.cs`
- Modify: `src/Umamusume.CoreBridge/Protocol/CallbackParser.cs`
- Modify: `src/UmaAssistantCore/CoreHandle.cpp`

**Interfaces:**

```cpp
struct ConnectionTimings
{
    // existing fields
    int max_attempts = 3;
    std::chrono::milliseconds retry_interval = 2s;
};

struct ConnectionFailure
{
    ConnectionErrorCode error_code;
    std::string phase;
    std::string message;
    std::int32_t attempt = 1;
    std::int32_t max_attempts = 1;
};
```

```csharp
public sealed record ConnectionFailedEvent(
    ulong OperationId,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message,
    int Attempt,
    int MaxAttempts) : ConnectionTerminalEvent(OperationId);
```

- [ ] **Step 1: Add failing default-policy and callback-field tests.**

Assert `ConnectionTimings{}.max_attempts == 3`, `retry_interval == 2000ms`, and parse a failed callback payload containing `attempt: 2` and `max_attempts: 3`.

- [ ] **Step 2: Run the focused tests and verify the new fields are missing.**

Run:

```powershell
ctest --preset release -R "EmulatorConnector|ManagedBridgeTests" --output-on-failure
```

Expected: compilation or assertion failure from the new contract tests.

- [ ] **Step 3: Add fields with aggregate-initializer-safe defaults.**

Keep existing three-field `ConnectionFailure{code, phase, message}` initializers valid. Emit attempt values from `CoreHandle::emit_failure` and require/validate them in the managed parser as positive integers with `attempt <= max_attempts`.

- [ ] **Step 4: Run the focused tests.**

Expected: existing callback and timing tests pass, including callbacks that use the default attempt values.

---

### Task 3: Make native target resolution and handshake retryable

**Files:**
- Test/modify: `tests/Connection/EmulatorConnectorTests.cpp`
- Modify: `src/UmaAssistantCore/Connection/EmulatorConnector.cpp`
- Modify: `src/UmaAssistantCore/Connection/EmulatorConnector_ResolveTarget.cpp`

**Interfaces:**
- `EmulatorConnector::connect()` retains its public signature.
- Add private retry classification/helpers only; do not expose new native ABI functions.
- Retry failures carry the final attempt and max-attempt values.

- [ ] **Step 1: Make the existing deterministic test timings use `max_attempts = 1`.**

This preserves the current one-script-result tests. Add separate timing helpers with `max_attempts = 3` and `retry_interval = 0ms` for retry tests.

- [ ] **Step 2: Add failing native tests for offline recovery, transient connect, exhaustion, and cancellation.**

Required scripts:

```cpp
// first devices: serial offline; then connect; then devices device; then normal handshake
// first connect failure; second connect success; then normal handshake
// three retryable failures; assert final attempt == 3 and no fourth runner call
// stop_token requested during retry interval; assert Canceled and no later call
```

Also assert `DeviceUnauthorized` and malformed identity do not retry.

- [ ] **Step 3: Extract the existing handshake body into one attempt path.**

The outer `connect()` validates once, loops from `1` through `max_attempts`, creates a fresh `ConnectedDevice` for each attempt, and restarts from `step_resolve_target` after retryable failure. Check cancellation before each delay. Append `attempt N/M` only to the returned failure message/metadata, not to error classification.

- [ ] **Step 4: Recover listed offline TCP targets.**

In `step_resolve_target`, when the target is `offline` and `is_tcp_endpoint(serial)` is true, run the same `adb connect` + ready-poll path used for absent TCP targets. Keep opaque offline/USB and unauthorized devices non-connectable/non-retryable as defined by the design.

- [ ] **Step 5: Classify failures by code and phase.**

Retry `DeviceOffline`, `DeviceNotReady`, `CommandTimedOut`, `ProcessStartFailed`, and transport `CommandFailed`/`DeviceUnavailable` from `list_devices`, `connect`, `ready_poll`, or handshake command phases. Return immediately for `InvalidArgument`, `AdbExecutableNotFound`, `DeviceUnauthorized`, `InvalidDeviceResponse`, and `Canceled`.

- [ ] **Step 6: Run the native connection tests.**

Run:

```powershell
ctest --preset release -R EmulatorConnectorTests --output-on-failure
```

Expected: all existing tests plus the new retry tests pass.

---

### Task 4: Convert managed ADB probing to cancellable async resolution

**Files:**
- Test/modify: `tests/UmamusumeWpfGui.Tests/Helper/EndpointResolverTests.cs`
- Modify: `src/UmamusumeWpfGui/Helper/IAdbRunner.cs`
- Modify: `src/UmamusumeWpfGui/Helper/AdbRunner.cs`
- Modify: `src/UmamusumeWpfGui/Helper/EndpointResolver.cs`

**Interfaces:**

```csharp
public interface IAdbRunner
{
    AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments);
    Task<AdbCommandResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
    // Keep RunDevices for existing callers and tests.
}

public sealed record EndpointResolutionPolicy(
    TimeSpan ReadyPollTimeout,
    TimeSpan PollInterval,
    int MaxAttempts,
    TimeSpan RetryInterval)
{
    public static EndpointResolutionPolicy Default { get; } = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250), 3, TimeSpan.FromSeconds(2));
}

public async Task<EndpointResolutionResult> ResolveAsync(
    string adbPath,
    string profileName,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Adapt existing resolver tests to async and add red polling/cancellation cases.**

Use an immediate fake `IAsyncDelay`. Cover existing ready device, connect→poll→ready, connect success but never ready, transient connect failure, exhausted endpoints, and cancellation during poll/retry.

- [ ] **Step 2: Run the focused resolver tests and verify the async API is absent.**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~EndpointResolverTests"
```

Expected: the new async tests do not compile or fail because the resolver currently performs one synchronous `get-state`.

- [ ] **Step 3: Add `RunAsync` to the runner contract and implement cancellable process execution.**

Use `Process.WaitForExitAsync`, concurrent `StandardOutput`/`StandardError` reads, the existing command timeout, and `Kill(entireProcessTree: true)` on timeout/cancellation. Cancellation must propagate as `OperationCanceledException`; timeout must return `TimedOut = true`.

- [ ] **Step 4: Implement `EndpointResolver.ResolveAsync`.**

Use `RunAsync` for every command, check the token before every command and delay, poll `adb devices` until the exact endpoint reports `device`, retry transient failures up to the policy limit, and append endpoint/command diagnostics for every failed attempt. A `connected` string alone never creates a verified endpoint.

- [ ] **Step 5: Run focused managed resolver tests.**

Expected: all resolver tests pass without real delays.

---

### Task 5: Wire async resolution and automatic startup continuation

**Files:**
- Test/modify: `tests/UmamusumeWpfGui.Tests/Helper/WinAdapterTests.cs`
- Modify: `src/UmamusumeWpfGui/Helper/IWinAdapter.cs`
- Modify: `src/UmamusumeWpfGui/Helper/WinAdapter.cs`
- Test/modify: `tests/UmamusumeWpfGui.Tests/ViewModels/SettingsViewModelTests.cs`
- Modify: `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.cs`
- Modify: `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.AutoStart.cs`

**Interfaces:**

```csharp
Task<EndpointResolutionResult> ResolveEndpointsAsync(
    string adbPath,
    string profileName,
    CancellationToken cancellationToken);
```

`WinAdapter` receives the existing `IAsyncDelay` dependency through IoC and passes it to `EndpointResolver`.

- [ ] **Step 1: Add red adapter and ViewModel tests.**

Cover `MuMuNxMain.exe` process discovery, cancellation propagation, auto-start launch followed by a later ready candidate, wait timeout, zero-wait single pass, and cancellation before a second pass.

- [ ] **Step 2: Convert `IWinAdapter`/`WinAdapter` to async and update every fake.**

Use `ResolveEndpointsAsync` and preserve the existing synchronous `RefreshEmulatorsInfo` and `GetAdbDevices` APIs until separately needed.

- [ ] **Step 3: Pass the real connection token.**

Replace the current `CancellationToken.None` at `SettingsViewModel.cs` endpoint resolution with the active token. Surface aggregated resolver diagnostics in the connection diagnostic instead of swallowing them in the catch-all branch.

- [ ] **Step 4: Make auto-start continue into normal connection.**

Change the auto-start helper to return a boolean indicating whether discovery should continue. After a successful launch, use `AutoStartEmulatorWaitSeconds` as the deadline, poll discovery asynchronously, apply the selected ADB path/endpoint, and let the existing `ConnectAsync` validation/native call continue. Do not recursively start a second connect operation or leave a poller running after return.

- [ ] **Step 5: Run focused WPF tests.**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~WinAdapterTests|FullyQualifiedName~SettingsViewModelTests"
```

Expected: all discovery, auto-start, cancellation, and existing ViewModel tests pass.

---

### Task 6: Add serialized post-connect health monitoring

**Files:**
- Create: `src/UmamusumeWpfGui/Services/IConnectionHealthMonitor.cs`
- Create: `src/UmamusumeWpfGui/Services/ConnectionHealthMonitor.cs`
- Modify: `src/UmamusumeWpfGui/Bootstrapper.cs`
- Modify: `src/UmamusumeWpfGui/ViewModels/SettingsViewModel.cs`
- Create: `tests/UmamusumeWpfGui.Tests/Services/ConnectionHealthMonitorTests.cs`

**Interfaces:**

```csharp
public sealed record ConnectionHealthTarget(
    string AdbPath,
    string Serial,
    string ProfileName);

public sealed record ConnectionHealthFailure(
    string Serial,
    ConnectionErrorCode ErrorCode,
    string Diagnostic);

public interface IConnectionHealthMonitor : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<ConnectionHealthFailure>? Failed;
    void Start(ConnectionHealthTarget target);
    Task StopAsync();
}
```

The monitor receives `IAdbRunner`, `IWinAdapter`, `IUmaService`, and `IAsyncDelay`. It probes `[-s, serial, get-state]` every 15 seconds. A transient failure invokes one bounded native reconnect using the stored target; an exhausted reconnect raises `Failed` and the owner sets `ConnectionState.Failed` with the diagnostic. `Start` cancels/replaces an existing monitor; `StopAsync` waits for the worker to exit.

- [ ] **Step 1: Add failing monitor lifecycle tests.**

Cover clean probe, transient probe failure followed by successful `IUmaService.ConnectAsync`, exhausted recovery, stop/dispose cancellation, replacement of an existing monitor, and no overlapping reconnect.

- [ ] **Step 2: Implement the monitor with one worker and linked cancellation.**

Use `PeriodicTimer` only if it can be fully controlled by the injected delay; otherwise use the delay seam. Never fire an unobserved task. Swallow no errors except cancellation during shutdown.

- [ ] **Step 3: Start/stop it from `SettingsViewModel`.**

Start after `HandleConnectSuccess` with `LastVerifiedConnection` values and `_draft.ConnectConfig`; stop before a new connection, cancellation, and `Dispose`. On failure update diagnostics and transition to `Failed` on the WPF dispatcher.

- [ ] **Step 4: Register the singleton monitor and run focused tests.**

Run:

```powershell
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj --filter "FullyQualifiedName~ConnectionHealthMonitorTests|FullyQualifiedName~SettingsViewModelTests"
```

---

### Task 7: Extend fake-ADB integration coverage

**Files:**
- Test/modify: `tests/Connection/UmaCallerTests.cpp`
- Modify: `tests/Connection/FakeAdb.cpp`

- [ ] **Step 1: Add a red fake-ADB offline-then-ready scenario.**

Use an environment variable such as `UMA_FAKE_ADB_OFFLINE_THEN_READY=1`. The fake returns the target as `offline` for the first `devices` invocation, returns a successful `connect`, then returns `device` for later lists; existing default behavior remains unchanged.

- [ ] **Step 2: Add the managed/native integration assertion.**

Run the integration host against the fake executable and assert the final structured result is `ConnectionSucceeded`, not `DeviceOffline`, with the expected identity and geometry.

- [ ] **Step 3: Run the integration test through CTest.**

Run:

```powershell
ctest --preset release -R ManagedBridgeTests --output-on-failure
```

Expected: the existing fake-ADB scenarios and offline recovery scenario pass.

---

### Task 8: Full verification and manual smoke

**Files:** No new source files; verification only.

- [ ] **Step 1: Build the native Release preset.**

```powershell
cmake --build --preset release
```

Expected: exit code `0`.

- [ ] **Step 2: Run all CTest and managed tests.**

```powershell
ctest --preset release -C Release --output-on-failure
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release
dotnet test tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj -c Release
```

Expected: all tests pass.

- [ ] **Step 3: Run real MuMu smoke when the device is online.**

```powershell
$adb = 'C:\Program Files\Netease\MuMuPlayer\nx_main\adb.exe'
& 'build\release\tools\Release\uma_connect_smoke.exe' $adb '127.0.0.1:16384'
```

Expected: structured connected-device output when MuMu is online. If MuMu is still offline, record that external blocker separately; fake-ADB tests must prove the retry path.

- [ ] **Step 4: Verify no unrelated files changed.**

```powershell
$env:GIT_MASTER='1'; git diff --check
$env:GIT_MASTER='1'; git status --short
```

Expected: only the approved source, test, spec, and plan files are modified; no commit is created.

## Completion Criteria

- All tasks above are checked off individually.
- Native and managed diagnostics are clean for changed files.
- Full CTest, CoreBridge tests, and WPF tests pass.
- Fake ADB proves offline recovery.
- MuMu process discovery recognizes `MuMuNxMain`.
- Auto-start can continue to a native connection without a second user click.
- ADB cancellation and shutdown leave no child process or background monitor.
- Existing behavior outside emulator linking is unchanged.
