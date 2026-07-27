# UmamusumeAss Connection and Control MVP — Implementation Specification

> **Status:** Implementation-ready S1 connection specification plus mandatory S2 control gate. Single source of truth.
> **Platform:** Windows 10/11, x64.
> **S1 Goal:** Ship a reliable ADB connection verifier for a real emulator: identify the exact device, wait for Android boot completion, and display identity and screen dimensions.
> **S2 Release Goal:** Ship a usable interactive assistant foundation: verified game package, live screenshot, correctly scaled tap/swipe input, and explicit device-loss handling. OCR and training automation are S3.

---

## 1. Executive Summary

UmamusumeAss is a Windows desktop assistant for Umamusume: Pretty Derby. Its connection layer follows the same architecture as MaaAssistantArknights: a C++20 Core DLL with a stable C ABI, loaded in-process by a C#/.NET 10 WPF GUI via P/Invoke. No HTTP backend, no web UI, no separate daemon process.

S1 proves an end-to-end ADB device handshake, not game automation. S2 turns that verified connection into a live controllable device session. The GUI has exactly two tabs (Log, Settings). All connection configuration and operations live in `SettingsView`, organized by a 160 px left navigation with three panels (Connection, Language, System). `ConnectView` and `ConnectViewModel` do not exist.

---

## 2. Binding Decisions

| Decision | Choice | Rationale |
|---|---|---|
| OS | Windows 10/11 | MAA-aligned; no Linux/macOS in scope |
| GUI framework | WPF (.NET 10, `net10.0-windows10.0.17763.0`) | Same as MAA `MaaWpfGui`; in-process DLL load |
| MVVM toolkit | Stylet | MAA-aligned; no Prism/ReactiveUI |
| Controls library | HandyControls | MAA-aligned; no MaterialDesignXaml |
| Tab count | 2 (Log, Settings) | Connection is a sub-page of Settings, not a top-level tab |
| Connection UI | `SettingsView` left-nav panel | No `ConnectView` or `ConnectViewModel` |
| Settings layout | 160 px left nav + scrollable right content | Three panels: Connection, Language, System |
| Core language | C++20 | Same as MAA `MaaCore` |
| Build system | CMake 3.28+ | Same as MAA |
| C ABI macros | `UMA_API_PORT` (import/export), `UMA_CALL` (calling convention), and combined `UMA_API` + `extern "C"` | Stable undecorated C exports; mirrors MAA's native boundary |
| String marshalling | `Marshal.PtrToStringUTF8` / `UTF8Pinned` | Avoid implicit string marshalling at the ABI boundary |
| Callback JSON | Versioned envelope with typed schemas | Each callback carries `"version": 1` and a `"type"` field |
| ADB policy | Reuse configured adb, never `kill-server` | The app does not own the ADB server unless it started one and user enabled cleanup |
| Target classification | Distinguish existing ADB serial from TCP endpoint | `adb connect` is valid only for a host-and-port target; USB and `emulator-####` serials are never passed to it |
| Device readiness | `get-state` plus boot-complete poll | `adb devices = device` alone is not proof that Android services are ready |
| Auto-reconnect | None in S1 | S1 does not implement automatic reconnect |
| Default AutoDetect | `true` (fill empty fields only unless `AlwaysAutoDetect` is on) | MAA-aligned |
| Language default | `en-US` | Invalid persisted values fall back deterministically |
| Theme | Uma Musume pink (#E91E8C) + gold + gradient background | Design system in section 6 |
| Packaging | Portable ZIP (flat layout) | CMake install + `dotnet publish --self-contained` |
| CI | Windows GitHub Actions (CMake preset + dotnet) | Connection-layer smoke test on real emulator required before S1 ships |
| Native startup | Explicit, fail-closed initialization | Resolve paths, call `UmaSetUserDir` and `UmaLoadResource`, then create a handle |
| Native shutdown | Synchronous callback-safe destruction | `UmaDestroy` cancels, joins workers, and guarantees no callback after it returns |
| Operation model | One active user operation per handle | Connect, verify, capture, and input reject overlap; the internal session monitor is serialized and never becomes a second user operation |
| Command execution | Executable path plus argument vector | Never pass a user-expanded shell command line to `CreateProcessW` |
| C++ runtime | Self-contained native deployment | Statically link the MSVC runtime or package its redistributable DLLs and test a clean PC |

### 2.1 Explicitly Excluded from S1

- `ConnectView`, `ConnectViewModel`, any third tab
- HTTP backend, web server, daemon process
- Screenshot capture, `screencap`, image decoding (required in S2)
- Touch input, `adb input`, Minitouch, MaaTouch (required in S2)
- Game automation (launch, stop, OCR, task pipeline; S3)
- Vendor DLL integration (MuMu `external_renderer_ipc`, LDPlayer `ldopengl64`)
- Registry-based emulator discovery (BlueStacks `bluestacks.conf`, LDPlayer `ldconsole`, Androws, MuMu uninstall key)
- WSA, AVD, vendor-specific extras
- ADB `kill-server` (unless the app started the ADB server and user explicitly enabled cleanup)
- Automatic reconnect or connected-session persistence
- Coordinate scaling (`ControlScaleProxy`)
- MaaFramework, adb-lite, OpenCV, ONNX Runtime

### 2.2 Functional Release Boundary

S1 is a connection verifier only. It must never be presented as a usable Umamusume automation assistant. A user-facing "usable assistant" release requires every S2 capability in section 4.8: a verified target-game package, a current screenshot, a normalized portrait coordinate space, tap and swipe execution, and a visible transition to `Disconnected` when the selected device disappears. S3 begins only after S2 is stable and adds launch, OCR, recognition, and task logic.

---

## 3. Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│  UmamusumeWpfGui.exe  (C# .NET 10 WPF / Stylet)         │
│  ┌────────────────────────────────────────────────────┐  │
│  │ SettingsViewModel    LogViewModel                  │  │
│  │ (connection + lang + system)                       │  │
│  ├────────────────────────────────────────────────────┤  │
│  │ UmaService (calls bridge)                          │  │
│  │ WinAdapter (process scan + adb devices)            │  │
│  │ ISettingsService / IConnectionStateService          │  │
│  │ ILocalizationService                               │  │
│  ├────────────────────────────────────────────────────┤  │
│  │ UmaCoreBridgeNative  (LibraryImport P/Invoke)      │  │
│  │ SafeUmaHandle         (SafeHandle → UmaDestroy)     │  │
│  └────────────────────────────────────────────────────┘  │
│                          │ P/Invoke                       │
│                          ▼                                │
│  UmamusumeCore.dll  (C++20, exported C ABI)              │
│  ┌────────────────────────────────────────────────────┐  │
│  │ UmaCaller.cpp    (thin C wrapper → C++ internals)  │  │
│  │ EmulatorConnector (ADB handshake state machine)    │  │
│  │ AdbCommandRunnerWin32 (CreateProcess + pipes)      │  │
│  │ ConnectionProfile  (JSON template expander)        │  │
│  └────────────────────────────────────────────────────┘  │
│                          │                                 │
│                          ▼                                 │
│  resource/connection.json  (General ADB profile)          │
└──────────────────────────────────────────────────────────┘
```

### 3.1 Layer Responsibilities

**UmamusumeCore.dll** (C++20) — owns ADB process execution, JSON profile expansion, and the connection handshake state machine. In S2 it also owns bounded PNG capture, frame lifetime, coordinate transformation, standard-ADB input, package verification, and device-loss monitoring. It exposes a stable C ABI through `UmaCaller.h`; it has no GUI dependency and no game automation logic.

**UmaCoreBridgeNative.cs** — P/Invoke declarations matching `UmaCaller.h`. Converts native callback JSON into C# events. Not used directly by ViewModels; they call `UmaService`.

**UmaService.cs** — The single GUI-side owner of `SafeUmaHandle`. Roots the native callback delegate for the handle lifetime, calls connection and S2 APIs, marshals callback JSON onto the WPF dispatcher, and exposes `ConnectAsync`, `CancelOperationAsync`, `VerifyGameAsync`, `CaptureAsync`, `TapAsync`, `SwipeAsync`, and `ConnectionChanged`. ViewModels depend on `IUmaService`.

`UmaService.InitializeAsync(appBaseDir, appDataDir)` is mandatory before any handle is created. It resolves canonical absolute paths, calls `UmaSetUserDir(appDataDir)`, calls `UmaLoadResource(appBaseDir)`, verifies `UmaGetVersion`, and only then calls `UmaCreate`. A failed initialization leaves the service unavailable, produces a localized startup error, and disables Connect; it must never continue with an uninitialized DLL. `UmaService.DisposeAsync()` requests cancellation, then calls blocking `UmaDestroy` only after the active operation has emitted its terminal event. The native runner is required to finish this path within the 10 s shutdown bound. If that invariant is violated, the handle is retained and the process records a fatal native-shutdown diagnostic rather than freeing a callback-capable handle.

**WinAdapter.cs** — GUI-layer emulator discovery. Uses `Process.GetProcesses()` to find known emulator EXEs, derives ADB path from process directory, runs `adb devices` to list connected serials. Never calls Core DLL for discovery.

**SettingsViewModel** — Sole owner of connection state in the GUI. Exposes `AdbPath`, `ConnectAddress`, `ConnectionState`, `LastVerifiedConnection`, `StatusText`, `ConnectCommand`, `CancelConnectCommand`, `SaveCommand`, and `DetectAdbConfig`. It also owns language selection and system info readouts. `LastVerifiedConnection` is immutable and separate from the editable draft; it is labelled “Last verified”, never represented as a live guarantee, and is cleared only by an explicit Forget action or app-data reset.

**LogViewModel** — Displays a timestamped list of callback JSON events received from Core. Color-coded: info (gray), success (pink), failure (red).

### 3.2 Calling Convention

```c
// UmaCaller.h — the only cross-language boundary header

#pragma once
#include <stdint.h>

#if defined(_WIN32)
#  define UMA_CALL __stdcall
#  if defined(UMA_DLL_EXPORTS)
#    define UMA_API_PORT __declspec(dllexport)
#  else
#    define UMA_API_PORT __declspec(dllimport)
#  endif
#else
#  define UMA_CALL
#  define UMA_API_PORT
#endif

#define UMA_API UMA_API_PORT UMA_CALL

typedef struct UmaHandleImpl* UmaHandle;

typedef struct UmaStartResult {
    uint64_t operation_id; // non-zero only when error_code == 0
    int32_t error_code;    // ConnectionErrorCode; 0 means start accepted
} UmaStartResult;

typedef void (UMA_CALL* UmaApiCallback)(
    int32_t message,
    const char* details_json,
    void* custom_arg);

#ifdef __cplusplus
extern "C" {
#endif
UMA_API_PORT const char* UMA_CALL UmaGetVersion(void);
UmaHandle UMA_API UmaCreate(UmaApiCallback callback, void* custom_arg);
void UMA_API UmaDestroy(UmaHandle handle);
int32_t UMA_API UmaSetUserDir(const char* utf8_path);
int32_t UMA_API UmaLoadResource(const char* utf8_path);
UmaStartResult UMA_API UmaConnectAsync(UmaHandle handle,
                                       const char* adb_path,
                                       const char* serial,
                                       const char* profile);
int32_t UMA_API UmaCancelConnect(UmaHandle handle, uint64_t operation_id);
int32_t UMA_API UmaCancelOperation(UmaHandle handle, uint64_t operation_id);
// S2 control-ready APIs. Each async call reports its result through UmaApiCallback.
UmaStartResult UMA_API UmaVerifyGameAsync(UmaHandle handle, const char* utf8_package_id);
UmaStartResult UMA_API UmaCaptureAsync(UmaHandle handle);
int32_t UMA_API UmaGetFramePngSize(UmaHandle handle, uint64_t frame_id, uint64_t* size);
int32_t UMA_API UmaCopyFramePng(UmaHandle handle, uint64_t frame_id,
                                uint8_t* destination, uint64_t capacity);
int32_t UMA_API UmaReleaseFrame(UmaHandle handle, uint64_t frame_id);
UmaStartResult UMA_API UmaTapAsync(UmaHandle handle, uint64_t frame_id,
                                   int32_t canonical_x, int32_t canonical_y);
UmaStartResult UMA_API UmaSwipeAsync(UmaHandle handle, uint64_t frame_id,
                                     int32_t x1, int32_t y1, int32_t x2, int32_t y2,
                                     int32_t duration_ms);
#ifdef __cplusplus
}
#endif
```

`UMA_DLL_EXPORTS` is defined only while building `UmamusumeCore.dll`; consumers receive `dllimport`. `UmaSetUserDir` and `UmaLoadResource` return `0` only for success. `UmaLoadResource` receives the application base directory and loads `<base>\\resource\\connection.json`; it validates the file before marking the runtime initialized. `UmaCreate` returns null before successful resource initialization or when `callback` is null.

`UmaConnectAsync`'s `profile` parameter is the UTF-8 **profile name** (for S1, `"General"`), not arbitrary JSON. Its `UmaStartResult` is the complete synchronous result: `error_code != 0` means no worker was started and no callback will occur; `error_code == 0` requires a non-zero `operation_id` and exactly one terminal callback. `Busy` is returned when a handle already owns an active user operation. `UmaCancelConnect` remains the S1 compatibility entry point; `UmaCancelOperation` is the idempotent cancellation API for every S1/S2 user operation. It returns `0` when cancellation was requested or the matching operation is already terminal, and `InvalidArgument` for another handle's or an unknown ID.

S2 uses the same start/result contract for verify, capture, tap, and swipe. Only one user operation may run per handle; the internal 5 s device monitor is serialized with it and never produces `Busy`. `UmaCaptureAsync` emits `FrameCaptured` metadata containing a non-zero `frame_id`, but never places PNG bytes in JSON. While that frame is retained, the bridge obtains its exact byte count with `UmaGetFramePngSize`, allocates a managed buffer, copies it with `UmaCopyFramePng`, then calls `UmaReleaseFrame` in a `finally` block. Native code retains at most one 16 MiB frame per handle; attempting to read an unknown, released, or superseded frame returns `InvalidArgument`. `UmaTapAsync` and `UmaSwipeAsync` require that retained `frame_id`, so input cannot use stale geometry.

All exported P/Invoke methods carry `[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]`; `UmaApiCallback` carries `[UnmanagedFunctionPointer(CallingConvention.StdCall)]`. Every input string is a NUL-terminated UTF-8 buffer. Native code never retains those input pointers after `UmaConnectAsync` returns. Native code catches all exceptions before crossing the C ABI; managed callback code catches all exceptions before returning to native code.

`UMA_API` combines `UMA_API_PORT` and `UMA_CALL` after ordinary return types. Pointer-returning `UmaGetVersion` uses MAA's split form so MSVC sees the export attribute before `const char*` and the calling convention after it. The macros keep the header portable to a future non-Windows toolchain.

### 3.3 P/Invoke UTF-8 Boundary

The generated P/Invoke declarations use `LibraryImport`, `SafeUmaHandle`, and temporary pinned UTF-8 buffers rather than `StringMarshalling.Utf8`. The callback delegate is held in a private `readonly` field for the entire `SafeUmaHandle` lifetime.

```csharp
[StructLayout(LayoutKind.Sequential)]
private readonly struct UmaStartResult(ulong operationId, int errorCode)
{
    public readonly ulong OperationId = operationId;
    public readonly int ErrorCode = errorCode;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
private delegate void UmaApiCallback(int message, IntPtr detailsJson, IntPtr customArg);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaConnectAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static unsafe partial UmaStartResult UmaConnectAsyncNative(
    SafeUmaHandle handle, byte* adbPath, byte* serial, byte* profile);

public unsafe Task<ConnectionResult> ConnectAsync(
    string adbPath, string serial, string profile, CancellationToken cancellationToken)
{
    using var adbPathUtf8 = new UTF8Pinned(adbPath);
    using var serialUtf8 = new UTF8Pinned(serial);
    using var profileUtf8 = new UTF8Pinned(profile);
    var start = UmaConnectAsyncNative(
        _handle, adbPathUtf8.Pointer, serialUtf8.Pointer, profileUtf8.Pointer);
    return RegisterStartResult(start, cancellationToken);
}
```

Before the native call, `UmaService` creates a per-handle **starting-operation buffer**. Callback delivery may race with the return from `UmaConnectAsync`, so the bridge synchronously copies and buffers callback JSON until `UmaStartResult` is available. For an accepted start, it binds the buffer to the returned non-zero `operation_id`, validates and replays events in arrival order, then routes later events directly by operation ID. A synchronous start failure must produce no callback; buffered data in that case is a native-contract violation and becomes a local bridge diagnostic. This prevents an immediate callback from leaving a `Task` incomplete. The cancellation registration is disposed when that operation reaches its first terminal event.

The native callback converts `details_json` with `Marshal.PtrToStringUTF8` before `Application.Current.Dispatcher.InvokeAsync` updates `SettingsViewModel` or publishes an event for `LogViewModel`.

The pointer passed as `details_json` is non-null, UTF-8, and valid only for the duration of the callback. `UmaService` copies it synchronously before queueing work to the Dispatcher. A callback delegate remains rooted until `UmaDestroy` has returned. `UmaDestroy` is blocking: it requests cancellation, terminates or waits for its child process and worker threads, emits at most one terminal result for each started operation, and guarantees no future callback for that handle. `SafeUmaHandle.ReleaseHandle` must therefore be reached only by the coordinated `UmaService` shutdown path, not while an operation is live.

### 3.4 Callback JSON Schema

Every callback payload is a UTF-8 JSON object with this envelope:

```json
{
  "version": 1,
  "operation_id": 42,
  "type": "ConnectionStarted | ConnectionProgress | ConnectionSucceeded | ConnectionFailed | GameVerified | GameVerificationFailed | FrameCaptured | InputSucceeded | InputFailed | DeviceDisconnected",
  "payload": { ... }
}
```

`operation_id` is the non-zero value returned in `UmaStartResult`. `UmaService` ignores callbacks whose operation ID is no longer active, so a late terminal event from a canceled operation cannot overwrite a newer UI state. It first validates that callback message ID, envelope `version`, `type`, and payload schema agree; malformed or oversized callback JSON becomes a local bridge diagnostic and never reaches a ViewModel.

**`ConnectionStarted`**: `payload` = `{}`

**`ConnectionProgress`**: `payload` = `{ "phase": "adb_devices | adb_get_state | adb_connect | ready_poll | boot_poll | android_id | android_version | wm_size" }`

**`ConnectionSucceeded`**: `payload` = `{
  "serial": "127.0.0.1:5555",
  "android_id": "0123456789abcdef",
  "android_version": "14",
  "width": 1920,
  "height": 1080,
  "physical_width": 1920,
  "physical_height": 1080,
  "size_source": "physical | override"
}`

**`ConnectionFailed`**: `payload` = `{
  "error_code": 6,
  "phase": "adb_devices",
  "message": "adb: device '127.0.0.1:5555' not found"
}`

Error codes are defined in section 4.4.

Message IDs in the C ABI: `UMA_MSG_CONNECTION_STARTED = 1`, `UMA_MSG_CONNECTION_PROGRESS = 2`, `UMA_MSG_CONNECTION_SUCCEEDED = 3`, `UMA_MSG_CONNECTION_FAILED = 4`.

S2 message IDs: `UMA_MSG_GAME_VERIFIED = 5`, `UMA_MSG_GAME_VERIFICATION_FAILED = 6`, `UMA_MSG_FRAME_CAPTURED = 7`, `UMA_MSG_INPUT_SUCCEEDED = 8`, `UMA_MSG_INPUT_FAILED = 9`, `UMA_MSG_DEVICE_DISCONNECTED = 10`.

**`GameVerified`**: `payload` = `{ "package_id": "…", "is_foreground": true | false | "unknown" }`.

**`GameVerificationFailed`**: `payload` = `{ "error_code": 14, "package_id": "…", "message": "…" }`.

**`FrameCaptured`**: `payload` = `{ "frame_id": 73, "width": 1080, "height": 1920, "geometry_generation": 4, "png_bytes": 123456, "captured_at": "RFC-3339 timestamp" }`. The PNG is retrieved only through the frame APIs above.

**`InputSucceeded` / `InputFailed`**: `payload` includes `frame_id`, `kind` (`"tap"` or `"swipe"`), requested canonical coordinates, final ADB coordinates, and on failure an error code/message.

**`DeviceDisconnected`**: `payload` = `{ "error_code": 15, "phase": "session_monitor", "message": "…" }`.

### 3.5 ADB Ownership Policy

- The app uses whatever `adb.exe` the user or auto-detect provides.
- Before connecting: run `adb devices` and build the exact serial/state map.
- If the target is `device`, confirm it with `adb -s <serial> get-state` and continue; if it is `offline` or `unauthorized`, return the dedicated error.
- Run `adb connect <endpoint>` only when the target is absent **and** it parses as a TCP endpoint: `host:port`, IPv4 `address:port`, or bracketed IPv6 `[address]:port`. Never run it for an existing ADB serial, including `emulator-####` and USB serials.
- The app never runs `adb kill-server` unless (a) the app itself started that ADB server process, and (b) the user explicitly enabled cleanup in settings. S1 does not implement this kill-server path.
- The app does not proactively start an ADB server. `adb devices` follows normal ADB client behavior and may attach to or start the configured ADB server; this must be logged as part of the command result.
- Every child ADB process inherits the configured process environment, including `ADB_SERVER_SOCKET`, `ADB_SERVER_PORT`, and `ADB_VENDOR_KEYS`. The runner never rewrites these values. Job-object cancellation must be validated on a real emulator to prove it terminates only the client process and does not terminate a reused ADB server.

The last point means the app cannot guarantee that it did not start an ADB server: `adb devices` is allowed to start one. S1 never attempts to kill a server. The UI and log describe this accurately as “ADB client may have started or reused a server”, not “the app never starts ADB”.

---

## 4. Connection Protocol (S1)

### 4.1 Handshake State Machine

The state machine runs inside `EmulatorConnector::connect()` in the Core DLL. Steps are strictly ordered. Each step uses a command expanded from `resource/connection.json` via `ConnectionProfile::expand()`.

Before step 1, preflight canonicalizes and validates that `adb_path` is an existing `.exe`, rejects empty or control-character-containing serial/profile values, and creates an `AdbInvocation { executable, arguments[] }`. The process runner must not execute an expanded user-controlled shell command line.

```
1. list_devices
   → arguments: `["devices"]`
   → Exit code must be 0. Parse stdout structurally: skip the fixed header, split tab-separated lines, and build an exact `serial -> state` map. Unknown or malformed lines are diagnostics, not eligible devices.

2. resolve_target (conditional)
   → If target state is `device`, run `[-s, AdbSerial, get-state]`; stdout must be exactly `device`.
   → If target state is `offline`, return `DeviceOffline`; if `unauthorized`, return `DeviceUnauthorized`.
   → If the target is absent and is a TCP endpoint, run `["connect", "[AdbSerial]"]`. Exit code must be 0 and combined stdout/stderr must report `connected` or `already connected`.
   → Poll `devices` every 250 ms until the exact selected serial has state `device`; otherwise return `DeviceUnavailable` or `DeviceNotReady`.
   → If the target is absent and is not a TCP endpoint, return `DeviceUnavailable` with the diagnostic `serial not found and is not connectable as host:port`; do not execute `adb connect`.

3. boot_poll
   → arguments: `["-s", "[AdbSerial]", "shell", "getprop", "sys.boot_completed"]`
   → Poll every 500 ms for trimmed output `1` for at most 60 s. While waiting, a transient shell failure remains a retryable `DeviceNotReady` diagnostic; a cancellation stops immediately.

4. android_id
   → arguments: `["-s", "[AdbSerial]", "shell", "settings", "get", "secure", "android_id"]`
   → Trim stdout; reject empty values and values that are not at least 8 hexadecimal characters. Error = InvalidDeviceResponse.

5. android_version
   → arguments: `["-s", "[AdbSerial]", "shell", "getprop", "ro.build.version.release"]`
   → Trim stdout; reject empty, control characters, and values not beginning with a digit. Error = InvalidDeviceResponse.

6. get_size
   → arguments: `["-s", "[AdbSerial]", "shell", "wm", "size"]`
   → Parse `Physical size: WIDTHxHEIGHT` and optional `Override size: WIDTHxHEIGHT`. Reject 0, overflow, or unparseable physical dimensions. `width`/`height` report override dimensions when present (the effective Android display); physical dimensions are included separately. Error = InvalidDeviceResponse.
```

### 4.2 Serial Matching Rules

Parsing `adb devices` output must match the serial exactly (no substring). It must preserve the literal second-column state for every valid tab-separated record. Only the literal `device` state is eligible for device queries; `offline` and `unauthorized` map to their dedicated errors, and every other state is unavailable.

### 4.3 `connect` Decision

Run `adb connect <endpoint>` only when the target is completely absent from the serial/state map and is a valid TCP endpoint. `emulator-5554`, USB serials, and any other opaque serial are query-only: if absent, they fail with `DeviceUnavailable` and a corrective message instead of an invalid `adb connect`. If the serial appears as `offline`, return `DeviceOffline`; if it appears as `unauthorized`, return `DeviceUnauthorized` and instruct the user to accept the RSA key fingerprint. Neither state is retried automatically in S1. A connect command succeeds only when its exit code is zero and its combined diagnostic output reports `connected` or `already connected`; then the state machine repeats `adb devices` until the exact serial reaches `device`, validates `get-state`, and waits for `sys.boot_completed = 1` before reading identity or display data.

### 4.4 Error Codes

| Code | Name | Meaning |
|---|---|---|
| 1 | `AdbExecutableNotFound` | `adb_path` does not point to an executable file |
| 2 | `ProcessStartFailed` | `CreateProcess` failed for the ADB command |
| 3 | `CommandTimedOut` | ADB command did not complete within the timeout |
| 4 | `DeviceUnauthorized` | Device rejected the RSA key; user must accept on device |
| 5 | `DeviceOffline` | Device is in `offline` state |
| 6 | `DeviceUnavailable` | `adb connect` failed or device is unreachable |
| 7 | `CommandFailed` | ADB command returned non-zero exit code |
| 8 | `InvalidDeviceResponse` | Parsed output is empty, malformed, or out of range |
| 9 | `Canceled` | Operation was canceled by the caller |
| 10 | `DeviceNotReady` | `adb connect` returned success but the exact serial did not become `device` before the ready timeout |
| 11 | `InvalidArgument` | Empty, malformed, or unsafe ADB path, serial, or profile passed to the API |
| 12 | `Busy` | A user operation is already active for this handle |
| 13 | `BootNotCompleted` | Android did not report `sys.boot_completed = 1` before the boot timeout |
| 14 | `TargetGameNotInstalled` | S2 target-package verification found none of the selected package IDs |
| 15 | `DeviceDisconnected` | A connected S2 session disappeared or ceased to report state `device` |

### 4.5 Cancellation and Reentrancy

- Every S1/S2 async entry point creates a monotonically increasing operation ID and checks that operation's atomic cancellation flag before each ADB command.
- `UmaCancelOperation(handle, operationId)` requests cancellation. `UmaCancelConnect` delegates to it for a connect operation. Completion is reported through that operation's single terminal callback with `error_code = Canceled`.
- Cancellation also signals the active process runner. The Win32 runner terminates the child process tree through a Job Object, closes its pipe readers, and joins its worker before the terminal callback. A cancel request therefore completes within the 10 s shutdown bound rather than waiting for a full command timeout.
- A second connection request is rejected by both native code (`Busy`) and the GUI while the current operation is active; it never destroys and recreates a handle while native work may still use it.
- The Connect button is disabled while a connection is in progress via `IAsyncCommand`.

### 4.6 Process Runner Contract

`AdbCommandRunnerWin32` accepts an executable path and a vector of already-separated arguments. It sets `lpApplicationName` to the absolute `adb.exe` path and constructs `lpCommandLine` with Windows-compatible argument quoting (including the executable as argv[0]). It does not invoke `cmd.exe`, PowerShell, or any shell. It concurrently drains stdout and stderr, continues draining after the 64 KiB combined diagnostic cap, records exit code/duration/timed-out/canceled flags, and avoids pipe deadlock. Command defaults are: `devices` 15 s, `connect` 30 s, `get-state`/device queries 15 s, ready polling 30 s at 250 ms intervals, boot polling 60 s at 500 ms intervals, and S2 capture 15 s with a 16 MiB binary cap; all are constants covered by tests. Timeouts and cancellation terminate the complete Job Object process tree. The runner assigns the process to its Job Object before it can do work, closes inherited pipe ends deterministically, and treats an inability to assign or terminate the job as a process-runner failure rather than silently leaking a child.

### 4.7 Connected Session Semantics

S1 success creates a verified snapshot, not a claim that the device remains connected forever. The Core records the exact `adb.exe`, serial, effective/physical display sizes, and verification time. It does not reconnect automatically.

S2 adds a session monitor while control is enabled. Every 5 s it runs `adb -s <serial> get-state`; any non-`device` response, command timeout, or process-start failure transitions the GUI to `Disconnected`, disables capture and input, retains the immutable last-verified snapshot, and emits `DeviceDisconnected`. It must not issue `adb connect`, alter the user's ADB server, or silently switch to another serial. Recovery is an explicit user Connect action.

### 4.8 S2: Control-Ready Device Session (Required for a Usable Assistant Release)

S2 uses standard ADB only; vendor DLLs, Minitouch, and MaaTouch remain out of scope. Its purpose is to prove that UmamusumeAss can observe and deliberately control the exact verified emulator before any OCR or game logic is added.

1. **Target-game verification**
   - `ConnectionSettings` adds `TargetPackageIds`, an ordered, user-visible list supplied by the selected Umamusume distribution profile. The app must not guess a package name or mark a generic Android device as game-ready.
   - For each configured ID, run `adb -s <serial> shell cmd package list packages <package-id>`; if `cmd package` is unavailable, fall back to `pm list packages <package-id>`. An exact `package:<package-id>` result selects the package; no match returns `TargetGameNotInstalled` without discarding the ADB snapshot.
   - S2 determines foreground status by parsing the package component from `adb -s <serial> shell dumpsys window windows`. If the platform does not expose a reliable current-focus record, report `unknown`, never `false`. It does not launch or force-stop the game; those actions belong to S3.

2. **Live screenshot**
   - Capture with argument vector `[-s, serial, exec-out, screencap, -p]`; do not use a shell pipe. Enforce a 15 s timeout and a 16 MiB response cap, validate PNG signature and dimensions, then decode to an immutable frame with a monotonically increasing frame ID and capture time.
   - The first successful frame must agree with the effective `wm size`. A mismatch is surfaced as a geometry diagnostic and blocks input until the session refreshes its display metrics. Capture is on demand in S2; continuous capture is an explicit later performance feature.

3. **Canonical coordinates and input**
   - The UI exposes a portrait canonical coordinate space of `1080 × 1920`. Each frame records the transform between canonical coordinates and the current raw screenshot/device coordinates, including rotation and letterboxing. No input is sent when that transform is unavailable or stale.
   - A tap uses `[-s, serial, shell, input, tap, x, y]`; a swipe uses `[-s, serial, shell, input, swipe, x1, y1, x2, y2, durationMs]`. Coordinates are rounded only after the transform and are clamped to the verified effective display rectangle.
   - Before every input, the session rechecks that it is `Connected`, targets the same serial, and has a frame/geometry generation matching the current session. It logs the requested canonical point and final ADB point, never touch content or credentials.

4. **S2 operator controls**
   - Settings shows `Verify game`, `Capture screen`, and a clearly marked manual input test panel. The panel requires a confirmation click for every test action; it contains no loop, scheduler, or autonomous game behavior.
   - Log records target serial, package-verification result, frame ID/dimensions, coordinate transform generation, input command result, and device-loss events.

---

## 5. UI Specification

### 5.1 Window Layout

```
┌────────────────────────────────────────────────────┐
│  [  📋 Log  ]  [  ⚙ Settings  ]     ← Tab bar     │
├────────────────────────────────────────────────────┤
│  Selected tab fills the window below the tab bar.  │
└────────────────────────────────────────────────────┘
```

Tab bar: 48 px high, bottom-aligned. Active tab: pink text + 2 px pink top bar. Inactive: gray text.

### 5.2 SettingsView (two-tab UI)

```
┌─────────────────────────────────────────────────────────┐
│  SettingsView                                            │
├──────────┬──────────────────────────────────────────────┤
│ 160 px   │  Right content (scrollable, 24 px padding)  │
│          │                                              │
│ • 连接   │  [Dynamic content based on selected menu]    │
│ • 语言   │                                              │
│ • 系统   │                                              │
│          │                                              │
└──────────┴──────────────────────────────────────────────┘
```

Left nav: each item is 16+10 px padding, 14 px font. Selected item: pink text + 2 px left pink indicator + `PrimaryLightestBrush` background. Unselected: gray text. `ItemsControl` bound to `SettingsViewModel.MenuItems`.

#### 5.2.1 Connection Panel

```
┌──────────────────────────────────────────────────┐
│  Connection Configuration                         │
│                                                   │
│  ADB Path: [C:\Android\platform-tools\adb.exe] [Browse] │
│  Serial:   [127.0.0.1:5555                ▼]    │
│  [Auto Detect]  ☐ Always auto-detect before connect │
│  Profile: [General ▼]                            │
│                                                   │
│            ┌──────────────────┐                   │
│            │     Connect      │                   │
│            └──────────────────┘                   │
│            Status: Ready                          │
│                                                   │
│  ┌──────── Device Information ─────────────────┐  │
│  │ Serial:         127.0.0.1:5555              │  │
│  │ Android ID:     0123456789abcdef            │  │
│  │ Android Ver:    14                          │  │
│  │ Resolution:     1920 × 1080                │  │
│  └─────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────┘
```

- ADB Path: `TextBox` + `Browse` button (`OpenFileDialog`, filter `adb.exe|*.*`)
- Serial: `ComboBox IsEditable=True`, bound to `ConnectAddressHistory`, dropdown contains recent addresses
- Auto Detect button: runs `WinAdapter.RefreshEmulatorsInfo()` + `GetAdbDevices()`, then offers only records in state `device` for selection
- Auto-detection policy: the persisted default `AutoDetectConnection=true` runs only when either editable connection field is blank and fills blank fields only. `AlwaysAutoDetectConnection=true` forces rediscovery and requires user confirmation before it overwrites a non-empty draft. If discovery fails while both fields are valid manual input, Connect continues with that manual pair.
- Save button: validates and persists the current editable draft without marking it connected; Connect persists the pair only after a successful handshake
- Profile: read-only `ComboBox` with only `"General"` in S1
- S2 target package IDs: an editable ordered list under an Advanced expander. Distribution profiles may prefill it, but the user can inspect and change every package ID before Verify game is enabled.
- Connect button: 200×60 px, pink gradient, disabled while connecting, shows `StatusText`. While connecting, a visible Cancel button invokes `CancelConnectCommand`; the original Connect button remains disabled until its terminal callback is processed.
- Device info card: binds to immutable `LastVerifiedConnection`, is headed **Last verified**, and has a Forget button. It is not rebound to the editable draft while a new connection is in progress.
- S2 adds a **Control readiness** card beneath Device Information. It shows `Disconnected | Connected | GameNotInstalled | GeometryMismatch`, the selected target package, the latest frame dimensions/time, and `Verify game`, `Capture screen`, and confirmation-gated manual tap/swipe test controls. All controls except Forget are disabled unless the current session is `Connected`.

When discovery finds multiple emulator candidates or multiple eligible serials, it displays `SelectionDialogView` / `SelectionDialogViewModel` over `DetectedConnectionCandidate { EmulatorName, AdbPath, Serial }` and does not overwrite either field until the user confirms. Canceling discovery keeps the editable draft unchanged. A detected ADB path is paired only with serials returned by that same ADB executable. Manual entry is always available, including when process discovery cannot inspect an elevated process.

#### 5.2.2 Language Panel

- Language `ComboBox`: `["en-US", "zh-CN"]`, selected item bound to `SelectedLanguage`
- Hint: "Changes take effect immediately."

#### 5.2.3 System Panel

Read-only fields: Core Version, Resource Path, Current ADB Path, Last Detected Emulator.

### 5.3 LogView

Timestamped list of callback events. Each entry shows the callback type and JSON payload. Colors: info (gray text), success (#E91E8C), failure (#F44336). Auto-scrolls to bottom on new entry unless user has scrolled up.

---

## 6. Design System

### 6.1 Color Palette

| Token | Value | Usage |
|---|---|---|
| `PrimaryBrush` | `#E91E8C` | Buttons, active tab, selected nav |
| `PrimaryLightBrush` | `#FF6B9D` | Hover states |
| `PrimaryLighterBrush` | `#FFB3D0` | Secondary backgrounds, dividers |
| `PrimaryLightestBrush` | `#FFE4EC` | Gradient start, selected nav background |
| `GoldBrush` | `#FFB300` | Accent, badges |
| `WindowBackgroundBrush` | `#FFF0F5` | Window background (mid-gradient) |
| `CardBackgroundBrush` | `#FFFFFF` | Card/panel backgrounds |
| `TextPrimaryBrush` | `#2D2D2D` | Body text |
| `TextSecondaryBrush` | `#888888` | Secondary text |
| `TextOnPrimaryBrush` | `#FFFFFF` | Text on primary buttons |
| `DividerBrush` | `#FFE4EC` | Dividers |
| `SuccessBrush` | `#4CAF50` | Connection success indicator |
| `ErrorBrush` | `#F44336` | Connection failure indicator |

Window background: linear gradient `#FFE4EC → #FFF0F5 → #FFFFFF` (top to bottom).

### 6.2 Typography & Spacing

- Body: 14 px, `TextPrimaryBrush`
- Headings: 16 px bold, `TextPrimaryBrush`
- Buttons: 18 px Connect, 14 px secondary
- Card padding: 16 px
- Border radius: 4 px inputs, 6 px buttons, 8 px cards

### 6.3 Controls

All via HandyControls theme overrides (no ControlTemplate rewrites):
- Button primary: pink gradient, white text, 6 px radius
- Button secondary: white background, pink border
- TextBox: white, 2 px light-pink border, focus turns `PrimaryBrush`
- ComboBox: same as TextBox; dropdown light-pink border
- CheckBox: pink fill when checked (HandyControls `ToggleButtonSwitch`)
- Card: white, 1 px light-pink border, 8 px radius, subtle shadow

---

## 7. Data Model & Services

### 7.1 ConnectionSettings

```csharp
public sealed class ConnectionSettings
{
    public string AdbPath { get; set; } = "";
    public string ConnectAddress { get; set; } = "";
    public bool AutoDetectConnection { get; set; } = true;
    public bool AlwaysAutoDetectConnection { get; set; } = false;
    public string ConnectConfig { get; set; } = "General";  // S1-only: rejects unknown values, falls back to "General"
    public string Language { get; set; } = "en-US";
    public List<string> ConnectAddressHistory { get; set; } = [];
    public List<string> TargetPackageIds { get; set; } = [];

    public void AddAddressToHistory(string address);
}
```

`ConnectConfig` setter in S1 rejects any value other than `"General"` and falls back silently. The ViewModel maintains an editable `ConnectionSettingsDraft` separate from the persisted last-known-good `ConnectionSettings`. `AdbPath` and `ConnectAddress` are saved only after a successful connection or an explicit Save action, so a failed or canceled edit cannot overwrite the last known-good pair. Save validates local syntax and stores an unverified pair; successful connect stores it as last-known-good. History is capped at 5 entries; only successful connections add an address, existing addresses move to front, and blank addresses are ignored. Language and UI-only settings persist independently of connection success.

### 7.2 Service Interfaces

```csharp
public interface ISettingsService {
    ConnectionSettings Load();
    void Save(ConnectionSettings settings);
}

public enum ConnectionState { Idle, Detecting, Connecting, Connected, Disconnected, Failed, Canceling }

public sealed record LastVerifiedConnection(
    string AdbPath, string Serial, string AndroidId, string AndroidVersion,
    int Width, int Height, int PhysicalWidth, int PhysicalHeight, DateTimeOffset VerifiedAt);

public sealed record ControlSessionSnapshot(
    string Serial, string? TargetPackageId, long GeometryGeneration,
    int? FrameWidth, int? FrameHeight, DateTimeOffset? CapturedAt,
    ConnectionState State);

public interface IConnectionStateService {
    ConnectionState State { get; }
    LastVerifiedConnection? LastVerifiedConnection { get; }
    ControlSessionSnapshot? ControlSession { get; }
    event EventHandler? StateChanged;
}

public interface ILocalizationService {
    string CurrentCulture { get; }
    event EventHandler<string>? LanguageChanged;
    void SwitchLanguage(string culture);
    void Initialize();    // load persisted language at startup
    string GetString(string key);
}
```

- `JsonSettingsService`: `System.Text.Json`, stores in `%APPDATA%/UmamusumeAss/connection_settings.json`
- `ConnectionStateService`: operation state machine + immutable last-verified snapshot + S2 control-session snapshot, shared singleton across ViewModels. `LastVerifiedConnection` is historical data; it is not an operation state.
- `LocalizationService`: swaps `Resources/Strings.{culture}.xaml` in `Application.Resources.MergedDictionaries`

### 7.3 WinAdapter (GUI-Layer Discovery)

```csharp
public interface IWinAdapter {
    DiscoveryResult RefreshEmulatorsInfo();
    AdbDevicesResult GetAdbDevices(string adbPath);
}

public sealed record DetectedEmulatorInfo(string EmulatorName, string? AdbPath);
public sealed record AdbDeviceRecord(string Serial, string State);
public sealed record DiscoveryResult(IReadOnlyList<DetectedEmulatorInfo> Candidates,
                                     IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
public sealed record AdbDevicesResult(IReadOnlyList<AdbDeviceRecord> Records,
                                      IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
```

**`RefreshEmulatorsInfo`**:
1. Enumerate processes via `Process.GetProcesses()`
2. Match process name against known table: `HD-Player` → BlueStacks, `dnplayer` → LDPlayer, `Nox` → Nox, `MuMuPlayer`/`MuMuNxDevice` → MuMuEmulator12, `MEmu` → XYAZ
3. From process directory, try the ordered candidate ADB paths below; pick the first that `File.Exists()`
4. Deduplicate by resolved ADB path; different process paths yield separate candidates

| Emulator profile | Process name | Ordered path relative to process directory |
|---|---|---|
| BlueStacks | `HD-Player` | `HD-Adb.exe`; `Engine\\ProgramFiles\\HD-Adb.exe` |
| LDPlayer | `dnplayer` | `adb.exe` |
| Nox | `Nox` | `nox_adb.exe` |
| MuMu 12 | `MuMuPlayer`, `MuMuNxDevice` | `..\\..\\..\\nx_main\\adb.exe`; `..\\vmonitor\\bin\\adb_server.exe`; `..\\..\\MuMu\\emulator\\nemu\\vmonitor\\bin\\adb_server.exe`; `adb.exe` |
| MEmu / XYAZ | `MEmu` | `adb.exe` |

**`GetAdbDevices`**:
1. Run `"{adbPath}" devices` capture stdout
2. Skip `"List of devices attached"` header and blank lines
3. Split by tab; preserve the literal second column as `State`
4. The UI offers only records whose state is exactly `"device"`, but diagnostics retain `offline`, `unauthorized`, unknown, malformed, timeout, and non-zero results

Discovery failures are structured results with diagnostics, not silently converted to an empty list. Access failures while reading `Process.MainModule`, missing candidate ADB paths, non-zero `adb devices`, timeouts, stderr diagnostics, and multiple candidates are represented separately. The Core and GUI share one exact `adb devices` parser contract: header ignored, tab-separated fields, literal state preservation, and exact serial comparison; the GUI must not use a looser `Contains("device")` parser.

No registry reads, config file parsing, or vendor DLL paths in S1.

---

## 8. Phased Implementation Plan

### Phase 1: C++ Core Skeleton (Tasks 1–2)

**Goal:** Buildable project with public connection types and testable error codes.

| Task | Files | Acceptance Gate |
|---|---|---|
| 1.1 Root CMake + core target | `CMakeLists.txt`, `cmake/warnings.cmake`, `src/UmaAssistantCore/CMakeLists.txt`, `src/UmaAssistantCore/Core.cpp` | `cmake --preset release` succeeds with the pinned MSVC toolset, `/MT` runtime, and pinned JSON/Catch2 dependencies |
| 1.2 Test harness | `tests/CMakeLists.txt`, `tests/Connection/ConnectionProfileTests.cpp` | `ctest --test-dir build/release` passes; dependency acquisition is reproducible from a clean checkout |
| 1.3 Public connection types | `include/UmaAssistant/Connection.hpp` | Tests verify `ConnectionErrorCode` enum distinctness, `ConnectionResult` uses `variant<ConnectedDevice, ConnectionFailure>` |

### Phase 2: ADB Protocol (Tasks 3–5)

**Goal:** Core DLL can connect to a real emulator (unit-tested with fakes, manually verifiable with smoke tool).

| Task | Files | Acceptance Gate |
|---|---|---|
| 2.1 JSON profile + template expansion | `resource/connection.json`, `src/UmaAssistantCore/Connection/ConnectionProfile.{hpp,cpp}`, update `tests/` | Profile expands the `get_size` argument vector with the exact serial and configured executable path; rejects unclosed placeholders |
| 2.2 ADB process runner (Win32) | `src/UmaAssistantCore/Connection/AdbCommandRunner.{hpp,cpp}` (Win32 impl) | Fake runner injectable; real runner uses executable + argument vector, `CreateProcessW`, concurrent stdout/stderr drains, Job Object cancellation, bounded output, and timeout; startup failure returns error code |
| 2.3 Handshake state machine | `src/UmaAssistantCore/Connection/EmulatorConnector.{hpp,cpp}` | Fake ADB runner covers existing serial `get-state` → boot poll → ID → version → size; TCP endpoint absent→connect→ready poll→boot poll; offline, unauthorized, non-zero `devices`, absent opaque serial without connect, boot timeout, blank/invalid ID or version, physical/override sizes, timeout, and cancellation at every step |
| 2.4 Smoke tool | `tools/connect_smoke.cpp` | `uma_connect_smoke.exe <adb> <serial>` exits 0 and prints device info with real emulator |
| 2.5 Target and boot tests | `tests/Connection/EmulatorConnectorTests.cpp` | Fakes cover existing serial `get-state`, TCP endpoint connect/ready/boot flow, absent opaque `emulator-####` and USB serial without connect, and boot timeout before any identity query |

### Phase 3: C ABI DLL (Task 6)

**Goal:** `UmamusumeCore.dll` loadable from any consumer via `UmaCaller.h`.

| Task | Files | Acceptance Gate |
|---|---|---|
| 3.1 UmaCaller.h + UmaCaller.cpp | `include/UmaAssistant/UmaCaller.h`, `src/UmaAssistantCore/UmaCaller.cpp`, `src/UmaAssistantCore/CoreRuntime.hpp` | Resource must load before `UmaCreate`; C ABI has `extern "C"`, import/export separation, explicit `StdCall`, and `UmaStartResult`; `UmaDestroy` cancels/joins and guarantees no post-return callback |
| 3.2 Build as shared library | Update `src/UmaAssistantCore/CMakeLists.txt` to `SHARED` | `UmamusumeCore.dll` produced; all existing C++ tests still pass |
| 3.3 C ABI lifecycle tests | `tests/Connection/UmaCallerTests.cpp`, `tests/Connection/UmaCallerCConsumer.c` | A C consumer links to the DLL and the post-build `UmaExportVerification` gate verifies exactly 15 undecorated names; immediate validation failure emits no callback; async connection emits the versioned sequence; cancellation emits exactly one `Canceled` terminal event; concurrent start returns `Busy` |

### Phase 4: C# P/Invoke Bridge (Task 7)

**Goal:** C# code can call DLL functions and receive typed events. (Phase 5 supplies the WPF `IEventDispatcher` adapter.)

| Task | Files | Acceptance Gate |
|---|---|---|
| 4.1 Bridge declarations | `src/Umamusume.CoreBridge/CoreBridge/UmaCoreBridgeNative.cs` | `NativeLibrary.TryLoad("UmamusumeCore.dll")` succeeds |
| 4.2 SafeHandle | `src/Umamusume.CoreBridge/CoreBridge/SafeUmaHandle.cs` | `ReleaseHandle` calls `UmaDestroy` |
| 4.3 UmaService | `src/Umamusume.CoreBridge/Services/UmaService.cs` | Explicit initialization loads packaged resources before handle creation; callback JSON is synchronously copied, schema-validated, buffered during start registration, then dispatched; cancellation, shutdown, and terminal events complete exactly one matching Task; a late event cannot update the active operation; marshalling uses `Marshal.PtrToStringUTF8` and explicit calling-convention attributes |

### Phase 5: WPF Application (Tasks 8–10)

**Goal:** Running GUI with Log and Settings tabs, connection works end-to-end.

| Task | Files | Acceptance Gate |
|---|---|---|
| 5.1 Project + theme | `.csproj`, `App.xaml`, `Res/Theme.xaml`, `Res/Themes/Light.xaml`, `Res/Style.xaml`, control styles | Builds; window shows Uma Musume pink theme |
| 5.2 Models + services | `Models/ConnectionSettings.cs`, `Models/LastVerifiedConnection.cs`, `Services/ISettingsService.cs`, `Services/JsonSettingsService.cs`, `Services/IConnectionStateService.cs` | Draft/last-known-good roundtrip tests pass; state transitions and immutable last-verified data are covered; language persists independently; default values correct |
| 5.3 SettingsViewModel | `ViewModels/SettingsViewModel.cs` | Tests cover menu navigation, draft versus last-verified state, Connect/Cancel lifecycle, manual versus automatic discovery policy, address history, and language load/fallback/switch |
| 5.4 SettingsView XAML | `Views/SettingsView.xaml` | Left nav 160 px; three sub-panels; all labels via `DynamicResource` |
| 5.5 LogViewModel + LogView | `ViewModels/LogViewModel.cs`, `Views/LogView.xaml` | Subscribes to `UmaService.ConnectionChanged`; displays typed JSON entries |
| 5.6 RootView (2 tabs) | `Views/RootView.xaml` | Exactly two `TabItem` entries (Log, Settings); no ConnectView reference |
| 5.7 Bootstrapper | `Main/Bootstrapper.cs` | Registers `ISettingsService`, `IConnectionStateService`, `ILocalizationService`, `IUmaService`, `IWinAdapter`, and the generic selection dialog; no standalone ConnectView or ConnectViewModel is registered |
| 5.8 WinAdapter | `Helper/WinAdapter.cs` | Process-scan test with fakes covers known emulators, inaccessible processes, unknown processes, ADB path derivation, exact `adb devices` parsing, stderr/non-zero/timeout, single and multiple selection behavior |
| 5.9 Localization | `Services/ILocalizationService.cs`, `Resources/Strings.en-US.xaml`, `Resources/Strings.zh-CN.xaml` | Tests verify: default culture, switch language, invalid fallback, null/empty fallback, event firing, `GetString` return |
| 5.10 Selection dialog | `Views/Dialogs/SelectionDialogView.xaml`, `ViewModels/Dialogs/SelectionDialogViewModel.cs` | Multiple ADB candidates require explicit selection; Cancel preserves the draft |
| 5.11 Control-readiness UI | `Models/ControlSessionSnapshot.cs`, `Views/SettingsView.xaml`, `ViewModels/SettingsViewModel.cs` | Shows package, frame, geometry and device-loss state; test controls remain disabled unless the exact session is connected |

### Phase 6: Packaging & Acceptance (Task 11)

**Goal:** Portable ZIP, CI, and smoke-test sign-off.

| Task | Files | Acceptance Gate |
|---|---|---|
| 6.1 CMake presets | `CMakePresets.json` | `cmake --preset release` configures Release build |
| 6.2 Package script | `tools/package.ps1` | Builds Core first, copies its install manifest into `dotnet publish --self-contained`, then generates `dist/UmamusumeAss-win-x64.zip`; fails on missing inputs and verifies EXE, Core DLL, all dependent native DLLs (or `/MT` static runtime), .NET runtime, and `resource/connection.json` relative layout |
| 6.3 Running docs | `docs/RUNNING.md` | Documents extract → launch → configure → connect flow |
| 6.4 CI workflow | `.github/workflows/build.yml` | Build + unit test + package-layout test run on every push; the hardware smoke test is a release gate run on a documented real-emulator machine |
| 6.5 Real-emulator smoke test | Manual | `uma_connect_smoke.exe` against real emulator (BlueStacks, LDPlayer, or MuMu 12) exits 0 with correct device info |

The real-emulator smoke test is a documented release gate run on a controlled Windows machine; it is not an ordinary GitHub-hosted CI job. CI builds, unit-tests, and validates package layout only.

---

### Phase 7: S2 Control-Ready Session (Tasks 12–15)

**Goal:** A verified Umamusume emulator can be observed and deliberately controlled through standard ADB. This is the minimum functional release gate for calling the app an assistant.

| Task | Files | Acceptance Gate |
|---|---|---|
| 7.1 Game verification + session monitor | `src/UmaAssistantCore/Session/DeviceSession.{hpp,cpp}`, `Services/UmaService.cs` | Exact configured package detection succeeds; monitor transitions to `Disconnected` after device loss without invoking `adb connect` or changing serial |
| 7.2 Screenshot capture | `src/UmaAssistantCore/Session/ScreenCapture.{hpp,cpp}`, `src/UmamusumeWpfGui/Models/DeviceFrame.cs` | `exec-out screencap -p` returns a bounded, valid PNG; decoded dimensions match current effective display metrics before the frame is usable |
| 7.3 Standard ADB input | `src/UmaAssistantCore/Session/AdbInput.{hpp,cpp}`, `ViewModels/SettingsViewModel.cs` | Confirmation-gated canonical tap and swipe produce the exact transformed `adb shell input` arguments; stale geometry, device loss, and out-of-bounds points send no input |
| 7.4 S2 real-emulator smoke tool | `tools/control_smoke.cpp` | On one supported real emulator with Umamusume installed: verify package, capture a valid current frame, perform a user-confirmed harmless tap in a designated test area, then detect emulator closure |

S2 must use standard ADB commands only. Introducing vendor DLLs or autonomous game actions is not a shortcut around these acceptance gates.

---

## 9. Acceptance Gates (Must Pass Before S1 Ships)

1. **All unit tests pass** — C++ Catch2 tests and C# xUnit tests. Zero failures.
2. **DLL load test** — `NativeLibrary.TryLoad("UmamusumeCore.dll")` succeeds from the packaged layout.
3. **C ABI lifecycle** — `UmaCreate` → `UmaGetVersion` → `UmaConnectAsync`/`UmaCancelConnect` → `UmaDestroy` produces a unique operation ID, a versioned terminal callback, and no leaks. A synchronous start failure returns a typed `UmaStartResult` and produces no callback.
4. **SettingsViewModel menu navigation** — Three items, select each, content panel updates.
5. **Connection roundtrip (fake)** — `SettingsViewModel.PerformConnectAsync()` with faked-success result populates immutable `LastVerifiedConnection` fields (serial, android_id, android_version, effective and physical size).
6. **Connection failure display** — `SettingsViewModel.PerformConnectAsync()` with faked failure shows error code and message in `StatusText`.
7. **Auto-detect with single emulator** — `WinAdapter` with one faked process + one `device` record fills both `AdbPath` and `ConnectAddress`; `offline` and `unauthorized` are surfaced as diagnostics, never selected.
8. **Language switch** — Toggle between en-US and zh-CN; all `DynamicResource` bindings update; runtime strings refresh via `LanguageChanged` event.
9. **Address history** — Successful connect adds serial to history (max 5); duplicates move to front.
10. **Real-emulator smoke test** — `uma_connect_smoke <adb> <serial>` exits 0 on BlueStacks, LDPlayer, or MuMu 12 (at least one). Reports serial, Android ID, Android version, resolution.
11. **Portable ZIP is complete** — Contains GUI EXE, Core DLL, .NET runtime, `resource/connection.json`. Launches without additional install steps.
12. **No `ConnectView` or `ConnectViewModel`** — Grep the source tree; zero references.
13. **Exactly 2 tabs** — `RootView.xaml` has exactly 2 `TabItem` elements.
14. **No `StringMarshalling.Utf8`** — All P/Invoke marshalling uses `Marshal.PtrToStringUTF8` or `UTF8Pinned`.
15. **No HTTP server, no `kill-server` by default, no auto-reconnect** — Grep confirms absence.
16. **Initialization gate** — deleting or corrupting packaged `resource/connection.json` prevents handle creation and shows a localized startup error.
17. **Shutdown safety** — cancel/close while any ADB phase is active; `UmaDestroy` returns within the 10 s shutdown bound and no callback occurs afterwards.
18. **Connect readiness** — a scripted `adb connect` success followed by non-`device` listings fails with `DeviceNotReady`; a later `device` listing succeeds.
19. **Clean-machine package test** — on a Windows VM without a system .NET runtime or VC++ redistributable, extracted ZIP launches and can load Core DLL.
20. **ABI and start-race test** — a C consumer links against `UmaCaller.h`; exported names are undecorated; an immediate callback racing the managed registration is buffered and completes exactly one Task.
21. **Interactive cancellation** — Connect exposes Cancel while active; cancel transitions to `Canceling`, then a single `Canceled` terminal result, without replacing the last-verified snapshot.
22. **Opaque serial safety** — a missing `emulator-5554` or USB serial produces `DeviceUnavailable` and the runner never receives an `adb connect` invocation.
23. **TCP endpoint flow** — a missing `127.0.0.1:5555` performs `adb connect`, then requires exact `device`, successful `get-state`, and `sys.boot_completed = 1` before success.
24. **Boot readiness** — a device that remains `device` but never reports boot completion fails with `BootNotCompleted`; no Android-ID or display command is executed first.
25. **Multi-instance selection** — different ADB executables or multiple eligible serials require an explicit selection and retain the selected `(adb path, serial)` pair.

## 9.1 Additional S2 Release Gates (Required Before Calling the App Usable)

26. **Target game** — a configured package is verified by exact package output; a missing package returns `TargetGameNotInstalled` and does not claim the device is game-ready.
27. **Current visual state** — a real emulator produces a valid bounded PNG whose dimensions match the current session geometry.
28. **Safe input** — a confirmed test tap/swipe reaches only the selected serial with transformed, clamped coordinates; no input is emitted when geometry is stale, the session is disconnected, or confirmation is absent.
29. **Device loss** — closing or disconnecting the selected emulator transitions to `Disconnected` within 10 s, disables control, preserves Last verified, and never reconnects or switches targets automatically.

---

## 10. Resource File: connection.json

```json
{
  "connection": [
    {
      "configName": "General",
      "baseConfig": null,
      "commands": {
        "list_devices": ["devices"],
        "get_state": ["-s", "[AdbSerial]", "get-state"],
        "connect": ["connect", "[AdbSerial]"],
        "boot_completed": ["-s", "[AdbSerial]", "shell", "getprop", "sys.boot_completed"],
        "android_id": ["-s", "[AdbSerial]", "shell", "settings", "get", "secure", "android_id"],
        "android_version": ["-s", "[AdbSerial]", "shell", "getprop", "ro.build.version.release"],
        "get_size": ["-s", "[AdbSerial]", "shell", "wm", "size"]
      }
    }
  ]
}
```

This is deliberately shaped like MAA's connection configuration: `connection` is an ordered profile list, each profile has `configName` and may inherit from `baseConfig`. S1 ships only `General`, but the loader must reject duplicate names, unknown bases, and inheritance cycles so S2 can add vendor profiles without a schema migration. Device-list parsing and TCP-endpoint classification are deliberately **not** configurable: sections 4.2 and 4.3 are the single structural contract, so a profile cannot accidentally hide `offline` or `unauthorized` states or pass an opaque serial to `adb connect`. The configured ADB executable is never represented in JSON command text: `ConnectionProfile::expand()` substitutes placeholders only inside the argument array and returns `AdbInvocation { adb_path, arguments }`. `[AdbSerial]` expands to one requested-device argument. Any placeholder left unexpanded (still containing `[` or `]`) after substitution causes the invocation to be rejected.

---

## 11. File Layout

```
UmamusumeAss/
├── CMakeLists.txt
├── CMakePresets.json
├── cmake/warnings.cmake
├── include/UmaAssistant/
│   ├── Connection.hpp          # ConnectedDevice, ConnectionErrorCode, ConnectionResult, ConnectionRequest
│   └── UmaCaller.h             # C ABI: UmaHandle, UmaApiCallback, UmaCreate, UmaDestroy, etc.
├── resource/connection.json    # General ADB profile
├── src/
│   ├── UmaAssistantCore/
│   │   ├── CMakeLists.txt
│   │   ├── Core.cpp
│   │   ├── CoreRuntime.hpp
│   │   ├── UmaCaller.cpp
│   │   └── Connection/
│   │       ├── AdbCommandRunner.hpp        # IAdbCommandRunner interface
│   │       ├── AdbCommandRunnerWin32.cpp    # CreateProcessW implementation
│   │       ├── ConnectionProfile.hpp
│   │       ├── ConnectionProfile.cpp
│   │       ├── EmulatorConnector.hpp
│   │       └── EmulatorConnector.cpp
│   │   ├── Session/
│   │       ├── DeviceSession.hpp            # package verification + loss monitor
│   │       ├── DeviceSession.cpp
│   │       ├── ScreenCapture.hpp            # bounded PNG capture + geometry validation
│   │       ├── ScreenCapture.cpp
│   │       ├── AdbInput.hpp                 # canonical-coordinate tap/swipe
│   │       └── AdbInput.cpp
│   ├── Umamusume.CoreBridge/
│   │   ├── Umamusume.CoreBridge.csproj
│   │   ├── CoreBridge/
│   │   │   ├── UmaCoreBridgeNative.cs
│   │   │   └── SafeUmaHandle.cs
│   │   └── Services/
│   │       └── UmaService.cs
│   └── UmamusumeWpfGui/
│       ├── UmamusumeWpfGui.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── Main/Bootstrapper.cs
│       ├── Models/ConnectionSettings.cs
│       ├── Models/ControlSessionSnapshot.cs
│       ├── Models/DeviceFrame.cs
│       ├── Services/
│       │   ├── ILocalizationService.cs
│       │   ├── IConnectionStateService.cs
│       │   ├── ISettingsService.cs
│       │   └── JsonSettingsService.cs
│       ├── Helper/
│       │   ├── WinAdapter.cs
│       │   └── ThemeHelper.cs
│       ├── ViewModels/
│       │   ├── SettingsViewModel.cs
│       │   ├── LogViewModel.cs
│       │   └── Dialogs/SelectionDialogViewModel.cs
│       ├── Views/
│       │   ├── RootView.xaml
│       │   ├── SettingsView.xaml
│       │   ├── LogView.xaml
│       │   └── Dialogs/SelectionDialogView.xaml
│       ├── Resources/
│       │   ├── Strings.en-US.xaml
│       │   └── Strings.zh-CN.xaml
│       └── Res/
│           ├── Theme.xaml
│           ├── Style.xaml
│           ├── Themes/Light.xaml
│           └── Styles/{Button,TextBox,ComboBox,CheckBox,ScrollBar}.xaml
├── tests/
│   ├── CMakeLists.txt
│   ├── Connection/
│   │   ├── ConnectionProfileTests.cpp
│   │   ├── EmulatorConnectorTests.cpp
│   │   └── UmaCallerTests.cpp
│   ├── Session/
│   │   ├── DeviceSessionTests.cpp
│   │   ├── ScreenCaptureTests.cpp
│   │   └── AdbInputTests.cpp
│   └── Umamusume.CoreBridge.Tests/
│       ├── Umamusume.CoreBridge.Tests.csproj
│       └── GlobalUsings.cs
├── tools/
│   ├── connect_smoke.cpp
│   ├── control_smoke.cpp
│   └── package.ps1
├── docs/
│   └── RUNNING.md
└── .github/workflows/build.yml
```

No `Views/ConnectView.xaml`, `ViewModels/ConnectViewModel.cs`, or `ConnectViewModelTests.cs` anywhere in the tree.

---

## 12. Self-Consistency Checks

- **Two tabs only**: RootView binds `LogView` and `SettingsView`. Connection is a sub-panel of SettingsView via left navigation.
- **SettingsViewModel owns connection**: Editable settings, `ConnectionState`, `LastVerifiedConnection`, `ConnectCommand`, `CancelConnectCommand`, and `DetectAdbConfig` live in `SettingsViewModel`. No `ConnectViewModel` exists.
- **Core → C ABI → C# bridge**: The only path from GUI to ADB is `SettingsViewModel` → `UmaService` → `UmaCoreBridgeNative` → `UmamusumeCore.dll` → `EmulatorConnector` → `AdbCommandRunnerWin32`. No HTTP, no named pipes, no direct process access.
- **C ABI boundary**: Every export is inside `extern "C"`; `UMA_API` distinguishes DLL export/import and includes `UMA_CALL`; P/Invoke and callback delegate declare the same calling convention.
- **Callback JSON versioned**: Every callback has `"version": 1` and a typed envelope.
- **ADB ownership**: The app calls `adb connect` only when the target is absent **and** parses as a TCP endpoint. It never calls `adb kill-server` unless the user explicitly opts in (not in S1).
- **No registry discovery in S1**: `WinAdapter` uses only `Process.GetProcesses()` and `File.Exists()`.
- **Dispatcher marshalling**: GUI callbacks from the native thread marshal to WPF dispatcher via `Application.Current.Dispatcher.InvokeAsync` in `UmaService`.
- **Usable-release boundary**: S1 verifies an ADB device. S2 additionally verifies the configured Umamusume package, captures a current frame, transforms coordinates, sends confirmation-gated standard-ADB input, and disables control on device loss. S3 alone may add OCR or autonomous game tasks.
