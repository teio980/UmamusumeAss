# Phase 4 Managed Bridge Design

**Date:** 2026-07-27  
**Scope:** C# P/Invoke bridge, safe native-handle ownership, managed operation routing, and bridge tests  
**Decision:** Build a UI-independent `net10.0` bridge library and an xUnit test project. The Phase 5 WPF application will reference the library and supply a WPF dispatcher adapter.

## Context

Phase 3 exposes a stable 15-function C ABI from `UmamusumeCore.dll`. An accepted asynchronous start returns a globally unique operation ID, but native callbacks may arrive before the start function returns. The managed bridge must therefore register a starting-operation buffer before entering native code, synchronously copy callback JSON, bind the returned operation ID, and replay buffered events in arrival order.

The repository currently contains no C# project. Creating the WPF application shell during Phase 4 would mix interop correctness with Phase 5 UI work. The bridge will instead compile and test independently of WPF while preserving the event-dispatch boundary needed by the GUI.

## Goals

- Declare all 15 native exports with exact ABI-compatible layouts and calling conventions.
- Initialize native resources before creating a handle and fail closed on any error.
- Own the native handle through a coordinated `UmaService` lifecycle.
- Route accepted operations by operation ID and complete each operation exactly once.
- Handle callbacks that race with native start registration without timing assumptions.
- Validate callback envelopes and payload schemas before publishing events.
- Test concurrency, cancellation, shutdown, UTF-8 marshalling, and DLL loading.

## Non-Goals

- No WPF window, ViewModel, XAML, Stylet, HandyControls, localization, or settings UI.
- No implementation of S2 screenshot, frame, package, tap, or swipe behavior.
- No automatic reconnect, HTTP service, daemon, OCR, or game automation.
- No change to the 15-function native ABI.

## Project Structure

```text
src/Umamusume.CoreBridge/
├── Umamusume.CoreBridge.csproj
├── Native/
│   ├── IUmaNativeApi.cs
│   ├── UmaCoreBridgeNative.cs
│   └── SafeUmaHandle.cs
├── Interop/
│   ├── UmaApiCallback.cs
│   ├── UmaStartResult.cs
│   └── UTF8Pinned.cs
├── Protocol/
│   ├── CallbackEnvelope.cs
│   ├── ConnectionEvents.cs
│   ├── ConnectionErrorCode.cs
│   └── CallbackParser.cs
├── Services/
│   ├── IEventDispatcher.cs
│   ├── IUmaService.cs
│   ├── StartOperationBuffer.cs
│   └── UmaService.cs
└── Diagnostics/
    └── BridgeDiagnostic.cs

tests/Umamusume.CoreBridge.Tests/
├── Umamusume.CoreBridge.Tests.csproj
├── Fakes/FakeUmaNativeApi.cs
├── InteropTests.cs
├── CallbackParserTests.cs
├── StartOperationBufferTests.cs
├── UmaServiceTests.cs
└── NativeLoadTests.cs
```

The production native API implements `IUmaNativeApi`. Tests inject `FakeUmaNativeApi`, which can invoke the captured callback before returning `UmaStartResult`, after returning, or from another thread. `IEventDispatcher` keeps WPF out of the bridge; tests use an inline dispatcher and Phase 5 supplies a WPF Dispatcher implementation.

## Native Boundary

`UmaCoreBridgeNative` uses source-generated `LibraryImport` declarations. Every export carries:

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
```

The callback delegate carries:

```csharp
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
```

`UmaStartResult` uses sequential layout with `ulong OperationId` at offset 0 and `int ErrorCode` at offset 8. Its Windows x64 ABI size is 16 bytes because the native structure retains 8-byte alignment. `UmaHandle` is represented by `SafeUmaHandle`. Input strings are explicitly encoded as NUL-terminated UTF-8 by `UTF8Pinned`; neither `StringMarshalling.Utf8` nor implicit string marshalling is permitted. Callback JSON is synchronously copied with `Marshal.PtrToStringUTF8` before the callback returns to native code.

The bridge declares all S2 exports so the managed ABI remains complete. During Phase 4, tests assert that the existing native stubs return `InvalidArgument`; the bridge does not simulate working S2 behavior.

## Initialization and Handle Ownership

`UmaService.InitializeAsync(appBaseDir, appDataDir)` performs this fail-closed sequence:

1. Canonicalize and validate both absolute paths.
2. Call `UmaSetUserDir(appDataDir)` and require success.
3. Call `UmaLoadResource(appBaseDir)` and require success.
4. Read and validate the non-empty UTF-8 native version.
5. Create the native handle with the rooted callback delegate.

No public operation is available before initialization succeeds. A failure leaves the service unavailable and disposes any managed resources created during that attempt.

`SafeUmaHandle.ReleaseHandle` calls `UmaDestroy`, but normal release is coordinated by `UmaService.DisposeAsync`. Disposal rejects new operations, requests cancellation for the active operation, waits for its first terminal event, and invokes blocking destruction away from the UI thread. The callback delegate remains rooted until destruction returns.

The coordinated shutdown has a 10-second bound. If the bound expires, the service records a fatal shutdown diagnostic, marks the `SafeUmaHandle` invalid without calling `UmaDestroy`, and retains the service and callback delegate for process lifetime. This intentionally leaks the native handle rather than freeing state that may still issue callbacks. Process exit remains the final containment boundary.

## Starting-Operation Buffer

Only one user operation may be starting or active per handle. `UmaService` installs one `StartOperationBuffer` under its operation lock before invoking an asynchronous native start.

The native callback performs only bounded synchronous work before returning:

1. Reject a null pointer.
2. Copy the NUL-terminated UTF-8 JSON into a managed string with `Marshal.PtrToStringUTF8` while the pointer is valid.
3. Measure the copied UTF-8 payload and reject it if it exceeds the configured callback limit.
4. Record the native message ID and copied JSON in arrival order.
5. Append to the unbound starting buffer or route to an already-bound operation.

The native DLL is trusted to honor its pointer-lifetime and NUL-termination contract. The managed size limit bounds retained and parsed data; it cannot make an invalid native pointer safe to dereference.

After the native start returns:

- `{0, error}` is a synchronous rejection. No task is registered. Any buffered callback is a native-contract violation and becomes a diagnostic.
- `{nonzero, 0}` is accepted. The service registers the operation completion source and cancellation callback, binds the starting buffer to that ID, and replays buffered events through the normal validator and router.
- `{0, 0}` or `{nonzero, error}` is an invalid native result and becomes a bridge failure.

Binding and callback acceptance use the same lock, so no event can be lost between draining the buffer and switching to direct routing. Replayed and directly routed events use the same parser, schema checks, state transitions, event publication, and terminal-completion path.

## Callback Validation and Routing

The parser validates:

- valid, bounded JSON with an object root;
- `version == 1`;
- a nonzero `operation_id`;
- exact native message ID to envelope `type` pairing;
- the required payload schema for the event type;
- known error codes and bounded strings and dimensions.

Connection event order is `ConnectionStarted`, zero or more `ConnectionProgress`, then exactly one `ConnectionSucceeded` or `ConnectionFailed`. The router rejects progress before start, events after a terminal event, duplicate terminal events, and events for another operation ID.

The first valid terminal event atomically marks the operation terminal, completes its task once, and disposes its cancellation registration. Duplicate, late, malformed, unknown-ID, and mismatched-ID callbacks produce structured diagnostics and never update an active or future UI state.

A malformed callback attributable to the active operation completes that operation with a managed bridge error so callers cannot wait forever. A callback that cannot safely be attributed to an active operation is diagnostic-only.

## Cancellation

A caller cancellation request invokes `UmaCancelOperation(handle, operationId)` only after the start result has bound a nonzero ID. Cancellation that arrives while the native start call is in progress is recorded and issued immediately after binding. Native `Success` means cancellation was requested or the operation was already terminal; `InvalidArgument` is surfaced as a diagnostic unless the managed operation already completed concurrently.

The cancellation registration is disposed by the first terminal event or by a synchronous start failure. `UmaCancelConnect` remains declared for ABI compatibility, while `UmaService` uses `UmaCancelOperation` for the unified operation model.

## Event Dispatch

Validation and operation completion do not depend on a UI thread. After an event is validated and operation state is updated, `UmaService` publishes the typed event through `IEventDispatcher`. Dispatch failure is contained as a diagnostic and does not call back into native code or change the already-determined operation result.

Phase 5 will provide a WPF implementation backed by `Application.Current.Dispatcher.InvokeAsync`. The native callback itself never waits for WPF; it only copies and records managed data.

## Diagnostics

`BridgeDiagnostic` is a typed local record containing a category, severity, optional operation ID, and safe message. Diagnostics cover native-contract violations, malformed callbacks, unknown or late events, cancellation failures, dispatcher failures, and fatal shutdown timeouts. They never include callback pointer values, raw frame bytes, credentials, or arbitrary unbounded native output.

## Testing Strategy

### Unit tests without the DLL

`FakeUmaNativeApi` scripts return values and callback schedules. Tests cover:

- callback before the start function returns, followed by ordered replay;
- callback after binding and direct routing;
- callbacks arriving concurrently with binding;
- synchronous rejection with no task and no callback;
- synchronous rejection with an illegal callback diagnostic;
- zero operation ID on success and nonzero operation ID on failure;
- mismatched operation ID;
- malformed, null, and oversized callback JSON;
- version, message/type, payload, and event-order mismatches;
- duplicate terminal and late callbacks;
- exactly-once task completion and event publication;
- cancellation before binding, after binding, and racing with terminal completion;
- cancellation-registration disposal after the first terminal event;
- coordinated disposal and no managed callback processing after destroy;
- dispatcher failure isolation.

### ABI and integration tests

- Assert `UmaStartResult` is 16 bytes on Windows x64, with field offsets 0 and 8.
- Verify UTF-8 NUL termination and non-ASCII round trips.
- Copy the built `UmamusumeCore.dll` beside the test host and require `NativeLibrary.TryLoad` success.
- In isolated child processes, verify initialization fails when `resource/connection.json` is missing or invalid so the process-global native runtime cannot contaminate later cases.
- Verify version retrieval and handle creation after valid resource loading.
- Run a Fake ADB connection through the real DLL and assert the complete typed callback sequence.
- Verify all S2 stubs return `InvalidArgument` without starting an operation.
- Dispose during an active Fake ADB operation and assert bounded shutdown and no post-destroy callback.

## Acceptance Gates

Phase 4 is complete only when:

1. `dotnet test` passes for the managed bridge test project.
2. Existing CMake configure, native build, CTest, and exact-export verification still pass.
3. The managed test host loads the packaged `UmamusumeCore.dll` successfully.
4. The callback-before-return test proves ordered replay and exactly-once completion without sleeps or scheduler-delay correctness mechanisms.
5. Malformed, mismatched, duplicate, and late events cannot update the active operation or published UI-facing state.
6. Cancellation and coordinated disposal pass their race tests and no callback is processed after native destruction returns.
7. Source search finds no `StringMarshalling.Utf8`, `DllImport` implicit string marshalling, `as any`, or suppressed diagnostics.

## Source-of-Truth Refinement

This design refines the Phase 4 file placement in `docs/superpowers.md`. The bridge types and `UmaService` move from `src/UmamusumeWpfGui/` into `src/Umamusume.CoreBridge/`; Phase 5 creates `src/UmamusumeWpfGui/`, references the bridge library, and supplies the WPF `IEventDispatcher`. The 15-function ABI, initialization order, callback schema, managed start buffer, and shutdown contracts remain unchanged.
