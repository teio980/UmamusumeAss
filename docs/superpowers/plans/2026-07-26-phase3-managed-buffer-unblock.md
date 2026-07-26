# Phase 3 Managed Buffer Unblock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the impossible callback-after-return requirement, preserve the 15-function C ABI, and complete Phase 3 under the approved operation-ID managed-buffer contract.

**Architecture:** Native code may deliver callbacks concurrently with `UmaConnectAsync` returning, but accepted operations have globally unique IDs and ordered, exactly-once terminal delivery. Phase 3 tests model a caller-side pre-registration buffer; Phase 4 must implement the same buffer in `UmaService` before it can be accepted.

**Tech Stack:** C++20, MSVC, CMake 3.28+, Catch2 3.15.2, nlohmann/json 3.12.0, Win32 DLL C ABI.

## Global Constraints

- Windows 10/11 x64 and MSVC Release remain the binding target.
- Keep exactly 15 undecorated exports from `UmaCaller.h`; do not add an activation API.
- A synchronous rejected start returns `{0, error_code}` and emits no callback.
- An accepted start returns `{operation_id, 0}` and emits ordered events with exactly one terminal callback.
- Callback delivery may race with the native start return; operation ID and caller-side buffering are the correctness boundary.
- `UmaDestroy` must still join native work and prohibit callbacks after return.
- Do not use sleep, timers, or scheduler yields to manufacture ordering.
- Do not implement the C# bridge in Phase 3.
- Do not create git commits, stage files, or modify the original checkout.

---

### Task 1: Revise the binding callback contract

**Files:**
- Modify: `docs/superpowers.md:223`
- Modify: `.superpowers/sdd/task-6-brief.md`
- Modify: `.superpowers/sdd/progress.md`

**Interfaces:**
- Consumes: approved design in `docs/superpowers/specs/2026-07-26-callback-ordering-design.md`.
- Produces: one unambiguous source-of-truth rule for all Phase 3 and Phase 4 workers.

- [ ] **Step 1: Replace the contradictory paragraph in the source of truth**

Use this exact contract in `docs/superpowers.md`:

```markdown
Before the native call, `UmaService` creates a per-handle **starting-operation buffer**. Callback delivery may race with the return from `UmaConnectAsync`, so the bridge synchronously copies and buffers callback JSON until `UmaStartResult` is available. For an accepted start, it binds the buffer to the returned non-zero `operation_id`, validates and replays events in arrival order, then routes later events directly by operation ID. A synchronous start failure must produce no callback; buffered data in that case is a native-contract violation and becomes a local bridge diagnostic. This prevents an immediate callback from leaving a `Task` incomplete. The cancellation registration is disposed when that operation reaches its first terminal event.
```

- [ ] **Step 2: Update the Task 6 brief**

Replace the native callback-after-return sentence with:

```markdown
- Callback delivery may race with the return from an accepted start. Every callback must carry the accepted `operation_id`; ordered buffering and replay are mandatory in Phase 4.
```

- [ ] **Step 3: Record the selected unblock decision**

Set the progress entry to `in progress` and name the Managed Buffer decision. Do not claim Phase 3 complete before Task 3 passes.

- [ ] **Step 4: Verify contradictory wording is gone**

Run:

```powershell
grep "must not invoke the callback until|callback-before-return" docs/superpowers.md .superpowers/sdd/task-6-brief.md
```

Expected: no binding requirement that native callbacks occur after the start function returns.

---

### Task 2: Replace scheduler timing with operation-ID buffer tests

**Files:**
- Modify: `src/UmaAssistantCore/CoreHandle.cpp`
- Modify: `tests/Connection/UmaCallerTests.cpp`

**Interfaces:**
- Consumes: `UmaStartResult UmaConnectAsync(...)` and callback envelope `{ version, operation_id, type, payload }`.
- Produces: native code without `StartGate`; a test harness that buffers callbacks before binding the returned operation ID.

- [ ] **Step 1: Write the failing contract test**

Add a `RegistrationBuffer` test helper with this behavior:

```cpp
class RegistrationBuffer
{
public:
    void accept(Message message)
    {
        std::lock_guard lock(mutex_);
        if (!operation_id_) buffered_.push_back(std::move(message));
        else route_locked(std::move(message));
    }

    void bind(std::uint64_t operation_id)
    {
        std::lock_guard lock(mutex_);
        operation_id_ = operation_id;
        for (auto& message : buffered_) route_locked(std::move(message));
        buffered_.clear();
    }
};
```

The callback must parse/copy JSON and call `RegistrationBuffer::accept`. The test calls `UmaConnectAsync`, then `bind(start.operation_id)`, waits for terminal completion, and asserts every routed event has the bound ID, preserves arrival order, and completes exactly once.

- [ ] **Step 2: Run the focused test before removing the gate**

Run:

```powershell
cmake --build build/release --config Release --target UmaCallerTests
build/release/tests/Connection/Release/UmaCallerTests.exe "accepted connect survives pre-registration callbacks"
```

Expected: FAIL because the current test suite/helper still asserts scheduler timing instead of buffer replay behavior.

- [ ] **Step 3: Remove `StartGate` and its false guarantee**

Delete `StartGate`, its `shared_ptr`, `wait()`, and `release()` use. Launch the worker only after handle state has been registered:

```cpp
handle->active_operation_id = operation_id;
handle->worker = std::jthread(
    [handle, operation_id, request = std::move(request), token](std::stop_token) mutable {
        try
        {
            run_connect(*handle, operation_id, std::move(request), token);
        }
        catch (...)
        {
            std::lock_guard worker_lock(handle->mutex);
            handle->active_operation_id = 0;
        }
    });
return {operation_id, UMA_SUCCESS};
```

Change `run_connect` to accept only the handle, operation ID, request, and cancellation token. It emits `ConnectionStarted` immediately when scheduled.

- [ ] **Step 4: Replace callback-before-return assertions**

Remove `start_call_active` and `callback_before_return`. Add assertions that buffered and directly routed callbacks share the same validation path, all IDs match, terminal count is one, and the ordered types/phases remain exact.

- [ ] **Step 5: Run focused tests**

Run:

```powershell
cmake --build build/release --config Release --target UmaCallerTests
build/release/tests/Connection/Release/UmaCallerTests.exe "accepted connect survives pre-registration callbacks"
build/release/tests/Connection/Release/UmaCallerTests.exe "accepted connect emits ordered versioned callbacks"
```

Expected: both pass.

- [ ] **Step 6: Stress the buffer contract**

Run the pre-registration test 100 times. Expected: 100/100 pass without sleeps or timing gates.

---

### Task 3: Verify and close Phase 3

**Files:**
- Modify: `.superpowers/sdd/task-6-report.md`
- Modify: `.superpowers/sdd/progress.md`
- Refresh: `.superpowers/sdd/task-6-diff.txt`

**Interfaces:**
- Consumes: revised source-of-truth contract and passing native buffer simulation.
- Produces: evidence-backed Phase 3 completion state suitable for starting Phase 4.

- [ ] **Step 1: Run full verification**

Run:

```powershell
cmake --preset release
cmake --build build/release --config Release
ctest --test-dir build/release -C Release --output-on-failure
```

Expected: configure/build exit 0 and all tests pass.

- [ ] **Step 2: Verify ABI exports**

Run `dumpbin /exports` against `build/release/src/UmaAssistantCore/Release/UmamusumeCore.dll` and count export rows.

Expected: exactly 15 undecorated exports, matching `UmaCaller.h`.

- [ ] **Step 3: Verify forbidden mechanisms and stale wording are absent**

Search changed source/spec files for `StartGate`, callback delay sleeps, the old native callback-after-return requirement, empty catches, and type-suppression patterns.

Expected: no matches except historical discussion in the approved design/report.

- [ ] **Step 4: Update durable evidence**

Record the exact test count, export list/count, stress count, TDD RED/GREEN evidence, and Managed Buffer decision in the report. Mark progress `complete; independent review pending`.

- [ ] **Step 5: Obtain independent review**

Provide the design, plan, refreshed report, and refreshed full working-tree diff to Oracle. Require explicit `SPEC`, `QUALITY`, and `CONCURRENCY` verdicts.

Expected: all three verdicts `APPROVED`; otherwise fix Critical/Important findings and repeat verification/review.

- [ ] **Step 6: Mark Phase 3 complete**

Only after approval, set `.superpowers/sdd/progress.md` to `Task 6 / Phase 3: complete` with fresh build/test/export evidence. Do not start Phase 4 in this task.
