# Callback Ordering and Managed Start Buffer Design

**Date:** 2026-07-26  
**Scope:** Phase 3 C ABI contract and mandatory Phase 4 bridge behavior  
**Decision:** Preserve the existing 15-function ABI and resolve start-registration races with a managed pre-call buffer.

## Context

`UmaConnectAsync` must return a nonzero operation ID for every accepted start. A native worker may become runnable before the ABI return instruction completes, and the existing function set contains no caller acknowledgment that can establish a strict happens-after edge between receiving `UmaStartResult` and callback delivery.

The previous contract simultaneously required native code not to callback before return and required the managed bridge to buffer callbacks that race with start registration. The first requirement is not enforceable with the current ABI; the second provides the required correctness boundary.

## Binding Contract

### Native start behavior

- `UmaConnectAsync` validates and copies all inputs synchronously.
- A rejected start returns `{0, error_code}` and emits no callback.
- An accepted start returns `{operation_id, 0}` with a globally unique, nonzero operation ID.
- Callback delivery may race with the return from `UmaConnectAsync`, including delivery while the native call is still unwinding.
- Every callback contains the accepted operation ID in the version-1 envelope.
- Events for one operation remain ordered: `ConnectionStarted`, zero or more `ConnectionProgress`, then exactly one terminal event.
- `Busy` starts emit no callback and cannot affect the active operation.
- Destruction still cancels and joins native work and guarantees no callback after `UmaDestroy` returns.

### Managed bridge behavior

Before invoking any native asynchronous start, `UmaService` creates one starting-operation buffer for the handle. The native callback synchronously copies the UTF-8 JSON before returning to native code and appends the parsed event to this buffer when no operation has been bound yet.

After the native call returns:

1. If `error_code != 0`, the bridge discards the empty starting buffer. Any buffered callback is a native-contract violation and becomes a local diagnostic.
2. If `error_code == 0`, the bridge requires a nonzero operation ID, binds the starting buffer to that ID, and registers the operation task.
3. Buffered events are replayed in arrival order through the same validation and completion path used by later callbacks.
4. A buffered event with a different operation ID, invalid schema, duplicate terminal event, or unsupported message/type pairing is rejected and recorded as a bridge diagnostic.
5. The buffer is removed after replay. Subsequent events route directly by operation ID.

Only one user operation may be starting or active per handle, so one pre-call buffer is sufficient. S2 asynchronous starts must reuse this exact mechanism.

## Native Implementation Changes

- Remove `StartGate` from `CoreHandle.cpp`; it does not establish the claimed ABI ordering guarantee.
- Start the worker after native state is fully registered and return the accepted operation ID without claiming callback-after-return ordering.
- Preserve synchronous validation, globally unique IDs, cancellation, Busy behavior, terminal idempotence, profile ownership, and joined destruction.
- Keep the DLL export set at exactly the existing 15 functions.

## Test Design

### Phase 3 native tests

- Replace the callback-before-return assertion with a start-registration harness that initially has no bound operation ID.
- The harness buffers early callbacks, binds the returned operation ID, replays buffered events, and verifies ordered completion exactly once.
- Run the harness repeatedly to exercise both early and later callback schedules.
- Keep tests for synchronous rejection with no callback, Busy, idempotent cancellation, destruction, UTF-8 paths, schema pairing, pure-C consumption, and exact exports.

### Mandatory Phase 4 tests

- Callback arrives before `UmaConnectAsyncNative` returns and is replayed after binding.
- Callback arrives after binding and routes directly.
- Synchronous start failure produces no task and no callback.
- Mismatched operation ID, malformed JSON, duplicate terminal, and late terminal events produce diagnostics without updating active UI state.
- Cancellation registration is disposed after the first terminal event.

## Error Handling

Native callback exceptions remain contained at the C ABI boundary. Managed callback code copies JSON synchronously and catches all exceptions before returning to native code. Buffer overflow, malformed payloads, or unsupported events fail closed: the active task receives a bridge error and the ViewModel is not updated from invalid data.

## Acceptance Gates

Phase 3 is complete when:

- The contradictory callback-after-return native requirement is removed from the source-of-truth specification.
- Native tests validate the race-tolerant operation-ID contract rather than scheduler timing.
- MSVC Release build and the complete CTest suite pass.
- The pure-C consumer passes.
- `dumpbin /exports` reports exactly the 15 expected undecorated C exports.
- Independent review approves the revised Phase 3 contract and implementation.

Phase 4 cannot be accepted unless it implements and tests the managed pre-call buffer defined here.

## Exclusions

- No activation export is added.
- No sleep, timer, scheduler yield, or other probabilistic callback delay is used as a correctness mechanism.
- No C# bridge implementation is included in the Phase 3 change; the bridge behavior is a mandatory Phase 4 gate.
- No S2 behavior is implemented beyond the existing safe stubs.
