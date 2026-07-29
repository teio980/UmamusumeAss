# Emulator Linking Reliability Design

## Goal

Make emulator linking reliable for the real Windows/MuMu workflow while preserving
the existing C# WPF and C++ native boundary. The implementation must handle the
observed `MuMuNxMain.exe` process, ADB startup races, transient `offline` states,
cancellation, and useful diagnostics instead of reporting a generic discovery
failure.

## Confirmed scope

This design covers all connection and discovery problems identified in the July 29
investigation:

1. Emulator process and ADB path discovery, including current MuMu process names.
2. Endpoint probing, ADB connection, ready polling, cancellation, and bounded retry.
3. Native connection handshake retry and precise retryable/non-retryable failures.
4. Automatic continuation after launching an emulator.
5. MAA-style named connection profiles with a shared General command base.
6. Post-connect health probing and explicit disconnected/failed state transitions.
7. Regression tests, fake-ADB scenarios, builds, and real smoke verification when a
   device is available.

## Explicit non-goals

This work does not implement the rest of MAA's controller stack: screencap method
benchmarking, RawByNc sockets, minitouch/maatouch, MuMu or LD vendor DLL extras,
coordinate scaling, game control operations, or task execution. Those are separate
features and are not required to make initial emulator linking reliable.

## Current evidence

- The live MuMu process was `MuMuNxMain.exe`, while the catalog only recognized
  `MuMuPlayer` and `MuMuNxDevice`.
- Release smoke with MuMu's ADB and `127.0.0.1:16384` returned
  `ERROR [resolve_target] (code 5): device is offline`.
- Immediately afterward, the same ADB server listed no devices, proving that a
  transient disappearance is a real environment case.
- Native target resolution returns immediately for `offline` and has no reconnect
  attempt.
- GUI endpoint resolution performs its own ADB commands, does not poll after
  `adb connect`, and passes `CancellationToken.None` from the ViewModel.
- MAA's `AdbController` has bounded reconnect attempts and retries failed commands
  after reconnecting.

## Architecture

### Responsibilities

The two existing layers remain, but their contract becomes explicit:

```text
WPF / SettingsViewModel
  └─ process discovery + ADB path selection
      └─ async EndpointResolver (candidate readiness)
          └─ IAdbRunner (bounded process calls)

Managed CoreBridge
  └─ owns native operation, callback ordering, cancellation, and health lifecycle

Native UmaAssistantCore
  └─ authoritative ADB handshake, retry policy, device identity and geometry
      └─ ConnectionProfile command expansion + Win32 process runner
```

The GUI may use ADB to find a candidate, but it must not treat that result as a
final connection. The native handshake always revalidates the serial and performs
all identity/boot/geometry checks before emitting success.

### Discovery

`EmulatorProfileCatalog` becomes data-complete for the currently supported Windows
processes. MuMu aliases include `MuMuNxMain`, `MuMuPlayer`, and `MuMuNxDevice`.
Profile matching remains case-insensitive. The existing relative ADB candidates and
known MuMu endpoint list remain, but every discovered candidate records the process
name and profile used so diagnostics can explain why a candidate was selected.

Discovery does not depend on arbitrary port scanning. It uses, in order:

1. A configured or cached ADB path when it still exists.
2. ADB candidates resolved relative to recognized emulator processes.
3. The finite profile endpoint list for the selected emulator.

### Async endpoint resolution

`EndpointResolver` is converted to an asynchronous operation with an injected delay
abstraction and cancellation token. `WinAdapter.ResolveEndpointsAsync` and the
ViewModel call site use the same token passed into `RunDiscoveryAsync`.

The resolver behavior is:

1. Run `adb devices` and return existing entries whose state is `device`.
2. If the requested profile has no ready entry, try each known TCP endpoint.
3. For each endpoint, run `adb connect` once per attempt.
4. After a successful command, poll `adb devices` until the endpoint is in state
   `device` or the endpoint-ready deadline expires.
5. Retry only transient command/transport failures, then continue to the next known
   endpoint.
6. Return all verified ready endpoints and all diagnostics collected along the way.

The resolver never reports an endpoint as verified based only on exit code from
`adb connect`. A successful connect message is not sufficient until `adb devices`
reports `device`.

### Native authoritative handshake

`EmulatorConnector` keeps its current explicit steps:

```text
resolve target → boot completed → android id → Android version → wm size
```

Target resolution changes as follows:

- A listed `device` is still checked with `get-state`.
- A listed `offline` TCP endpoint is treated as transient first. The connector
  retries discovery/connect within the retry policy instead of returning immediately.
- A missing TCP endpoint runs `adb connect` followed by the existing ready poll.
- A missing opaque serial never runs `adb connect`; it is polled only when the
  endpoint is expected to appear through the current ADB server.
- After any retryable command failure during the handshake, the whole attempt is
  restarted from target resolution. This prevents stale device identity or geometry
  from being carried into a later attempt.

The retry loop checks the stop token before every command and delay. It never retries
invalid arguments, missing executables, unauthorized devices, malformed identity,
or malformed display responses.

### Retry policy

The native `ConnectionTimings` gains explicit policy values with these defaults:

- `max_attempts = 3`
- `retry_interval = 2 seconds`
- existing command and ready-poll deadlines remain the upper bounds for one attempt

The managed endpoint resolver uses the same observable policy defaults and receives a
test-time policy object so tests do not sleep in real time. Policy values are not
read from user input in this change; they are protocol defaults to avoid an unsafe
configuration surface.

Retryable failures are:

- TCP endpoint absent, offline, or not ready yet.
- ADB connect command failure or timeout.
- ADB command timeout during the initial handshake.
- A device disappearing between list and query.

Non-retryable failures are:

- Empty/control-character arguments.
- Missing/non-executable ADB path.
- Unauthorized device.
- Invalid android ID, Android version, or display geometry.
- Unknown profile or command-template expansion failure.
- User cancellation.

### Profiles

`resource/connection.json` keeps the existing structured argument-array format and
adds named profiles inheriting from `General`:

- `General`
- `MuMuEmulator12`
- `LDPlayer`
- `BlueStacks`
- `Nox`
- `XYAZ`
- `WSA`
- `Androws`

All profiles initially inherit the same safe command set. This provides MAA-style
selection and future override points without pretending that vendor screenshot or
input extensions already exist. `ConnectionSettings.ConnectConfig` accepts these
known profile names and no longer silently converts every non-General value to
General. Unknown or empty values still fall back safely to General at the managed
settings boundary.

### Automatic startup continuation

When auto-start is enabled and no usable running candidate exists:

1. Persist only the configured emulator executable path.
2. Start the emulator.
3. If launch succeeds, use `AutoStartEmulatorWaitSeconds` as the bounded readiness
   window.
4. During that window, asynchronously re-run process/ADB discovery and endpoint
   readiness checks.
5. As soon as a candidate is ready, continue into the normal Native connection.
6. If the window expires or cancellation is requested, return to `Disconnected` with
   the last diagnostic and do not leave a background poller running.

If the configured wait is zero, one immediate rediscovery pass is made and then the
operation fails with a useful diagnostic if the emulator is not ready.

### Health monitoring

After a successful connection, a managed health monitor starts with the verified ADB
path and serial. It runs a bounded `get-state` probe at a fixed interval while the
state is Connected. The monitor:

- stops on disposal, explicit disconnect, or a new connect operation;
- ignores a single transient probe failure and performs bounded recovery through the
  same endpoint resolver;
- transitions to `Disconnected` or `Failed` with a precise diagnostic when recovery
  fails;
- never starts a second Native connect concurrently with an active operation.

The monitor is intentionally a state/diagnostic guard, not a replacement for future
per-command MAA reconnect behavior in the unimplemented task-control layer.

## Error and callback behavior

Native failures retain `ConnectionErrorCode`, phase, message, and retry attempt in
the existing structured callback payload. Managed parsing preserves these fields.

GUI discovery diagnostics are aggregated rather than discarded. The ViewModel shows
the final failure plus the most useful last command/endpoint detail. Catch blocks in
the discovery path no longer replace every exception with the same generic text.

State transitions are single-owner and serialized:

```text
Disconnected → Detecting → Connecting → Connected
       │             │           │          │
       └─────────────┴───────────┴──── Failed/Disconnected
```

Cancellation always ends in `Disconnected`, while an exhausted retry policy ends in
`Failed` and retains the diagnostic.

## Testing strategy

### Managed tests

- `EmulatorProfileCatalogTests`: `MuMuNxMain` alias and profile mapping.
- `EndpointResolverTests`: existing device, connect-to-ready polling, transient
  offline, endpoint fallback, timeout, cancellation, and command diagnostics.
- `WinAdapterTests`: `MuMuNxMain.exe` process path resolution and deduplication.
- `SettingsViewModelTests`: auto-start launch → rediscovery → Native connect,
  cancellation during startup, timeout diagnostics, and no concurrent operation.
- New health-monitor tests: clean probe, transient failure recovery, exhausted
  recovery, disposal cancellation, and no duplicate connect.

### Native tests

- Offline listed TCP device recovers through a scripted connect/list sequence.
- Connect command transient failure retries and then succeeds.
- Retry exhaustion returns the final phase and attempt detail.
- Cancellation during retry interval returns `Canceled` without more runner calls.
- Full handshake restarts after a retryable identity/query transport failure.
- Every named profile expands inherited General commands.

### Integration and manual verification

- Existing fake-ADB integration host gains an offline-then-ready scenario.
- Release CTest and managed test suites must pass.
- Manual MuMu smoke uses the installed MuMu ADB and the actual discovered serial when
  the emulator is online.
- MinGW smoke must be run with its configured runtime PATH or replaced by the
  verified Release smoke binary; loader failures are reported separately from ADB.

## Verification gates

Before declaring the work complete:

1. All changed C# and C++ files have clean diagnostics.
2. Focused managed and native tests pass.
3. Full CTest and .NET test suites pass.
4. Release build passes.
5. Real MuMu smoke either succeeds or records a current external blocker such as the
   emulator remaining offline; fake-ADB tests must still prove the retry behavior.
6. Existing user changes are preserved and no commit is created without an explicit
   request.
