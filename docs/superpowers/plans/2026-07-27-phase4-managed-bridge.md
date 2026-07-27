# Phase 4 Managed Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and test a UI-independent .NET 10 bridge that loads `UmamusumeCore.dll`, safely owns its handle, converts connection callbacks into typed events, and survives callback/start/cancel/shutdown races.

**Architecture:** `Umamusume.CoreBridge` is a strict `net10.0` class library. A sealed partial native adapter owns all 15 `LibraryImport` declarations, while `UmaService` depends on `IUmaNativeApi` and an injected event dispatcher. Before every asynchronous native start, the service installs a raw-callback buffer; after `UmaStartResult` binds the operation ID, buffered and later callbacks pass through one parser and router.

**Tech Stack:** Windows 10/11 x64, .NET SDK 10.0.301, C# 14, source-generated `LibraryImport`, `SafeHandle`, `System.Text.Json`, xUnit 2.9.2, `xunit.runner.visualstudio` 3.0.2, `Microsoft.NET.Test.Sdk` 17.11.1, CMake 3.28+, CTest.

## Global Constraints

- Follow `docs/superpowers/specs/2026-07-27-phase4-managed-bridge-design.md` and the C ABI in `include/UmaAssistant/UmaCaller.h`.
- Keep the native export set at exactly 15 functions; do not alter the ABI.
- Target `net10.0`; pin SDK `10.0.301` with `rollForward: latestPatch`.
- Enable C# 14, nullable analysis, .NET analyzers, unsafe blocks, and warnings as errors.
- Use `[LibraryImport]` plus `[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]` on every import.
- Use `[UnmanagedFunctionPointer(CallingConvention.StdCall)]` on `UmaApiCallback`.
- Use explicit NUL-terminated UTF-8 buffers and `Marshal.PtrToStringUTF8`; never use implicit string marshalling or `StringMarshalling.Utf8`.
- Limit retained callback JSON to 65,536 UTF-8 bytes after synchronous pointer copying.
- Support connection callback message IDs 1-4 in Phase 4. Treat callback IDs 5-10 as native-contract violations while S2 starts remain synchronous `InvalidArgument` stubs.
- Use `TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously)`.
- Do not use `Thread.Sleep`, timing delays, scheduler yields, or retry loops as concurrency correctness mechanisms.
- Do not add WPF, Stylet, HandyControls, screenshots, input, OCR, auto-reconnect, or game automation.
- Production code always follows a witnessed RED test. Configuration-only steps are the only non-behavior exception.
- Commit commands below run only if the execution session has explicit user authorization to create implementation commits.

---

### Task 1: Managed project baseline and source-of-truth alignment

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `src/Umamusume.CoreBridge/Umamusume.CoreBridge.csproj`
- Create: `tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj`
- Create: `tests/Umamusume.CoreBridge.Tests/GlobalUsings.cs`
- Modify: `docs/superpowers.md:667-675, 820-835`

**Interfaces:**
- Consumes: installed .NET SDK `10.0.301`.
- Produces: buildable `Umamusume.CoreBridge` and `Umamusume.CoreBridge.Tests` projects; Phase 4 paths in the source of truth point at the independent bridge library.

- [ ] **Step 1: Confirm the pinned SDK exists**

Run:

```powershell
dotnet --list-sdks
```

Expected: output includes `10.0.301`.

- [ ] **Step 2: Create the SDK and compiler configuration**

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the class-library project**

`src/Umamusume.CoreBridge/Umamusume.CoreBridge.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Umamusume.CoreBridge</RootNamespace>
    <AssemblyName>Umamusume.CoreBridge</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Umamusume.CoreBridge.Tests" />
    <InternalsVisibleTo Include="Umamusume.CoreBridge.IntegrationHost" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the xUnit project**

`tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <RootNamespace>Umamusume.CoreBridge.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.2">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Umamusume.CoreBridge\Umamusume.CoreBridge.csproj" />
  </ItemGroup>
</Project>
```

`GlobalUsings.cs` contains only:

```csharp
global using Xunit;
```

- [ ] **Step 5: Align the binding specification**

Change Phase 4 paths from `src/UmamusumeWpfGui/...` to `src/Umamusume.CoreBridge/...`, add `tests/Umamusume.CoreBridge.Tests/`, and state that Phase 5 supplies the WPF `IEventDispatcher` adapter. Do not change the ABI, callback, or shutdown requirements.

- [ ] **Step 6: Verify the empty baseline**

Run:

```powershell
dotnet build src/Umamusume.CoreBridge/Umamusume.CoreBridge.csproj -c Release
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release
```

Expected: both commands exit 0 with zero warnings; the test command reports no failed tests.

- [ ] **Step 7: Commit if explicitly authorized**

```powershell
git add global.json Directory.Build.props src/Umamusume.CoreBridge/Umamusume.CoreBridge.csproj tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj tests/Umamusume.CoreBridge.Tests/GlobalUsings.cs docs/superpowers.md
git commit -m "build: add managed bridge project baseline"
```

---

### Task 2: ABI value types and explicit UTF-8 ownership

**Files:**
- Create: `tests/Umamusume.CoreBridge.Tests/InteropTests.cs`
- Create: `src/Umamusume.CoreBridge/Interop/UmaStartResult.cs`
- Create: `src/Umamusume.CoreBridge/Interop/UmaApiCallback.cs`
- Create: `src/Umamusume.CoreBridge/Interop/UTF8Pinned.cs`
- Create: `src/Umamusume.CoreBridge/Interop/RawCallback.cs`
- Create: `src/Umamusume.CoreBridge/Protocol/ConnectionErrorCode.cs`

**Interfaces:**
- Produces: `UmaStartResult`, `UmaApiCallback`, `UTF8Pinned`, `RawCallback`, and `ConnectionErrorCode` with exact native-compatible values and lifetime rules.
- Consumes: native layouts and constants from `include/UmaAssistant/UmaCaller.h:21-58`.

- [ ] **Step 1: Write the failing ABI and UTF-8 tests**

Create tests with these exact assertions:

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge.Tests;

public sealed class InteropTests
{
    [Fact]
    public void UmaStartResult_matches_windows_x64_layout()
    {
        Assert.Equal(16, Marshal.SizeOf<UmaStartResult>());
        Assert.Equal(0, Marshal.OffsetOf<UmaStartResult>(nameof(UmaStartResult.OperationId)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<UmaStartResult>(nameof(UmaStartResult.ErrorCode)).ToInt32());
    }

    [Theory]
    [InlineData("General")]
    [InlineData("赛马娘-テスト")]
    public unsafe void UTF8Pinned_round_trips_and_is_nul_terminated(string value)
    {
        using var pinned = new UTF8Pinned(value);
        Assert.Equal(value, Marshal.PtrToStringUTF8((IntPtr)pinned.Pointer));
        Assert.Equal(0, pinned.Bytes[^1]);
    }

    [Fact]
    public void Callback_declares_stdcall()
    {
        var attribute = typeof(UmaApiCallback).GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(CallingConvention.StdCall, attribute.CallingConvention);
    }

    [Fact]
    public void Error_codes_are_complete_and_distinct()
    {
        int[] values = Enum.GetValues<ConnectionErrorCode>().Select(static value => (int)value).ToArray();
        Assert.Equal(Enumerable.Range(0, 16), values.Order());
    }
}
```

- [ ] **Step 2: Run RED and verify the expected compile failure**

Run:

```powershell
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release --filter FullyQualifiedName~InteropTests
```

Expected: FAIL because the five production types do not exist.

- [ ] **Step 3: Implement the exact ABI types**

Use these declarations:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal readonly struct UmaStartResult(ulong operationId, int errorCode)
{
    public readonly ulong OperationId = operationId;
    public readonly int ErrorCode = errorCode;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void UmaApiCallback(int message, IntPtr detailsJson, IntPtr customArg);

internal readonly record struct RawCallback(int MessageId, string Json);
```

`ConnectionErrorCode` is an `int` enum with every value from `Success = 0` through `DeviceDisconnected = 15`, matching `UmaCaller.h` exactly.

Implement `UTF8Pinned` as a sealed unsafe disposable class:

```csharp
internal sealed unsafe class UTF8Pinned : IDisposable
{
    private GCHandle _pin;
    private bool _disposed;

    public UTF8Pinned(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Bytes = Encoding.UTF8.GetBytes(value + '\0');
        _pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
    }

    internal byte[] Bytes { get; }
    internal byte* Pointer => _disposed
        ? throw new ObjectDisposedException(nameof(UTF8Pinned))
        : (byte*)_pin.AddrOfPinnedObject();

    public void Dispose()
    {
        if (_disposed) return;
        _pin.Free();
        _disposed = true;
    }
}
```

- [ ] **Step 4: Run GREEN and the full managed suite**

Run the filtered command from Step 2, then:

```powershell
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release
```

Expected: all tests pass with zero warnings.

- [ ] **Step 5: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Interop src/Umamusume.CoreBridge/Protocol/ConnectionErrorCode.cs tests/Umamusume.CoreBridge.Tests/InteropTests.cs
git commit -m "feat: add managed ABI types and UTF-8 pinning"
```

---

### Task 3: Safe handle and all 15 native declarations

**Files:**
- Modify: `tests/Umamusume.CoreBridge.Tests/InteropTests.cs`
- Create: `src/Umamusume.CoreBridge/Native/IUmaNativeApi.cs`
- Create: `src/Umamusume.CoreBridge/Native/SafeUmaHandle.cs`
- Create: `src/Umamusume.CoreBridge/Native/UmaCoreBridgeNative.cs`

**Interfaces:**
- Consumes: Task 2 interop types.
- Produces: safe high-level `IUmaNativeApi`; `SafeUmaHandle` whose sole release path calls its injected destroy delegate once; a production adapter containing exactly 15 raw imports.

- [ ] **Step 1: Add failing SafeHandle and import-contract tests**

Add tests that create a handle with `(IntPtr)42`, increment a counter from its destroy delegate, call `Dispose()` twice, and assert the counter equals 1. Add reflection that collects `LibraryImportAttribute.EntryPoint` from private methods in `UmaCoreBridgeNative` and compares the sorted set with the 15 names in `UmaCaller.h`.

- [ ] **Step 2: Run RED**

Run:

```powershell
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release --filter FullyQualifiedName~InteropTests
```

Expected: FAIL because `SafeUmaHandle`, `IUmaNativeApi`, and `UmaCoreBridgeNative` do not exist.

- [ ] **Step 3: Define the safe high-level native interface**

Use these exact method shapes:

```csharp
internal interface IUmaNativeApi
{
    string GetVersion();
    SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg);
    int SetUserDir(string path);
    int LoadResource(string path);
    UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile);
    int CancelConnect(SafeUmaHandle handle, ulong operationId);
    int CancelOperation(SafeUmaHandle handle, ulong operationId);
    UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId);
    UmaStartResult Capture(SafeUmaHandle handle);
    int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size);
    int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination);
    int ReleaseFrame(SafeUmaHandle handle, ulong frameId);
    UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y);
    UmaStartResult Swipe(SafeUmaHandle handle, ulong frameId, int x1, int y1, int x2, int y2, int durationMs);
}
```

- [ ] **Step 4: Implement `SafeUmaHandle`**

Derive from `SafeHandleZeroOrMinusOneIsInvalid`. Store `Action<IntPtr>? _destroy`, use `Interlocked.Exchange(ref _destroy, null)` in `ReleaseHandle`, invoke the returned delegate once, and return `false` if the delegate throws. Add `internal IntPtr Abandon()` that captures `DangerousGetHandle()`, calls `SetHandleAsInvalid()`, clears `_destroy`, and returns the raw pointer for timeout containment.

- [ ] **Step 5: Declare all imports and wrappers**

`UmaCoreBridgeNative` is `internal sealed unsafe partial class`. Add one private static partial import for every exported name:

```csharp
[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaGetVersion")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial IntPtr GetVersionNative();

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaCreate")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial IntPtr CreateNative(UmaApiCallback callback, IntPtr customArg);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaDestroy")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial void DestroyNative(IntPtr handle);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaSetUserDir")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int SetUserDirNative(byte* path);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaLoadResource")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int LoadResourceNative(byte* path);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaConnectAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial UmaStartResult ConnectNative(SafeUmaHandle handle, byte* adbPath, byte* serial, byte* profile);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaCancelConnect")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int CancelConnectNative(SafeUmaHandle handle, ulong operationId);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaCancelOperation")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int CancelOperationNative(SafeUmaHandle handle, ulong operationId);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaVerifyGameAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial UmaStartResult VerifyGameNative(SafeUmaHandle handle, byte* packageId);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaCaptureAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial UmaStartResult CaptureNative(SafeUmaHandle handle);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaGetFramePngSize")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int GetFramePngSizeNative(SafeUmaHandle handle, ulong frameId, ulong* size);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaCopyFramePng")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int CopyFramePngNative(SafeUmaHandle handle, ulong frameId, byte* destination, ulong capacity);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaReleaseFrame")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial int ReleaseFrameNative(SafeUmaHandle handle, ulong frameId);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaTapAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial UmaStartResult TapNative(SafeUmaHandle handle, ulong frameId, int x, int y);

[LibraryImport("UmamusumeCore.dll", EntryPoint = "UmaSwipeAsync")]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
private static partial UmaStartResult SwipeNative(SafeUmaHandle handle, ulong frameId, int x1, int y1, int x2, int y2, int durationMs);
```

Public interface wrappers create `UTF8Pinned` instances inside `using` scopes. `GetVersion` converts with `Marshal.PtrToStringUTF8` and throws `InvalidOperationException` for null or empty. `Create` returns `new SafeUmaHandle(raw, DestroyNative)`. `CopyFramePng` pins the provided span only for the native call and passes its exact length as `ulong`.

- [ ] **Step 6: Run GREEN**

Run the filtered Interop tests, then the full managed suite. Expected: exact 15-name reflection set, destroy count 1, and zero failures.

- [ ] **Step 7: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Native tests/Umamusume.CoreBridge.Tests/InteropTests.cs
git commit -m "feat: add safe native bridge declarations"
```

---

### Task 4: Strict connection callback parser

**Files:**
- Create: `tests/Umamusume.CoreBridge.Tests/CallbackParserTests.cs`
- Create: `src/Umamusume.CoreBridge/Diagnostics/BridgeDiagnostic.cs`
- Create: `src/Umamusume.CoreBridge/Diagnostics/ManagedBridgeException.cs`
- Create: `src/Umamusume.CoreBridge/Diagnostics/CallbackProtocolException.cs`
- Create: `src/Umamusume.CoreBridge/Protocol/CallbackEnvelope.cs`
- Create: `src/Umamusume.CoreBridge/Protocol/ConnectionEvents.cs`
- Create: `src/Umamusume.CoreBridge/Protocol/CallbackParser.cs`

**Interfaces:**
- Produces: typed connection events for IDs 1-4 and `CallbackProtocolException` for every malformed, mismatched, or S2 callback.
- Consumes: `RawCallback` and `ConnectionErrorCode`.

- [ ] **Step 1: Write table-driven RED tests**

Cover these valid pairs and payloads:

| ID | Type | Required payload |
|---|---|---|
| 1 | `ConnectionStarted` | empty object |
| 2 | `ConnectionProgress` | `phase`, one of the eight documented phase names |
| 3 | `ConnectionSucceeded` | serial, android_id, android_version, width, height, physical_width, physical_height, size_source |
| 4 | `ConnectionFailed` | error_code 1-15, phase, non-empty message |

Add individual tests rejecting null/empty JSON, payload over 65,536 UTF-8 bytes, non-object root, missing fields, wrong field types, `version != 1`, `operation_id == 0`, message/type mismatch, unknown progress phase, non-positive dimensions, unknown error code, and every message ID 5-10.

Use a representative assertion:

```csharp
[Fact]
public void Parse_rejects_message_type_mismatch()
{
    var raw = new RawCallback(1, """{"version":1,"operation_id":7,"type":"ConnectionFailed","payload":{}}""");
    var error = Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(raw));
    Assert.Equal(7UL, error.OperationId);
}
```

- [ ] **Step 2: Run RED**

Expected: compile failure because parser and event types do not exist.

- [ ] **Step 3: Implement immutable protocol types**

Use `public abstract record ConnectionEvent(ulong OperationId)`. `ConnectionStartedEvent` and `ConnectionProgressEvent` derive from it. Add `public abstract record ConnectionTerminalEvent(ulong OperationId) : ConnectionEvent(OperationId)`; `ConnectionSucceededEvent` and `ConnectionFailedEvent` derive from the terminal base. Define `ConnectionPhase` and `DisplaySizeSource` enums instead of publishing unchecked strings. `CallbackEnvelope` remains internal and owns a cloned `JsonElement Payload` so no disposed `JsonDocument` escapes.

`BridgeDiagnostic` contains `DiagnosticCategory`, `DiagnosticSeverity`, optional operation ID, and bounded safe message. `CallbackProtocolException` contains nullable `OperationId` and no raw JSON. `ManagedBridgeException` is the public operation failure raised when a callback attributable to the active operation violates the bridge contract; it contains the diagnostic category and optional operation ID, never raw JSON.

- [ ] **Step 4: Implement one strict parser path**

`CallbackParser.Parse(RawCallback raw)` first enforces the 65,536-byte limit, parses with `JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = Disallow, MaxDepth = 16 }`, validates the envelope, selects the exact parser from message ID, and returns one typed event. IDs 5-10 throw `CallbackProtocolException` with category `NativeContractViolation`.

- [ ] **Step 5: Run GREEN and full tests**

Run:

```powershell
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release --filter FullyQualifiedName~CallbackParserTests
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release
```

Expected: all parser cases and the full suite pass.

- [ ] **Step 6: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Diagnostics src/Umamusume.CoreBridge/Protocol tests/Umamusume.CoreBridge.Tests/CallbackParserTests.cs
git commit -m "feat: add strict connection callback parsing"
```

---

### Task 5: Raw callback start buffer

**Files:**
- Create: `tests/Umamusume.CoreBridge.Tests/StartOperationBufferTests.cs`
- Create: `src/Umamusume.CoreBridge/Services/StartOperationBuffer.cs`

**Interfaces:**
- Produces: a lock-protected one-time buffer that atomically switches from buffering raw callbacks to direct routing.
- Consumes: `RawCallback`.

- [ ] **Step 1: Write RED tests for ordering and the bind race**

The wished-for API is:

```csharp
var routed = new ConcurrentQueue<RawCallback>();
var buffer = new StartOperationBuffer(routed.Enqueue);
buffer.Accept(first);
buffer.Accept(second);
buffer.Bind(42);
Assert.Equal([first, second], routed);
Assert.Equal(42UL, buffer.OperationId);
```

Add tests for empty bind, append after bind, second bind rejection, and a deterministic concurrency case using two `Barrier` instances so `Accept` and `Bind` overlap without sleeps. Assert every unique callback appears exactly once.

- [ ] **Step 2: Run RED**

Expected: compile failure because `StartOperationBuffer` does not exist.

- [ ] **Step 3: Implement the minimal buffer**

Use a private lock, `List<RawCallback>`, `Action<RawCallback> route`, nullable operation ID, and states `Starting`, `Replaying`, `Direct`, `Rejected`. `Accept` appends while `Starting` or `Replaying`; only `Direct` captures the route under lock and invokes it outside the lock. `Bind(nonzeroId)` changes `Starting → Replaying`, then repeatedly moves the current queue to a local batch, routes that batch outside the lock, and rechecks the queue. It changes `Replaying → Direct` only while holding the lock and only when the queue is empty, so a callback cannot overtake replay. `Reject()` changes `Starting → Rejected`, returns all buffered callbacks for contract diagnostics, and rejects later acceptance. A second bind/reject throws `InvalidOperationException`. Expose read-only `OperationId`, `BufferedCount`, and state for service logic and tests.

- [ ] **Step 4: Run GREEN and a repeat stress command**

Run the filtered tests, then repeat them 100 times:

```powershell
1..100 | ForEach-Object { dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release --no-build --filter FullyQualifiedName~StartOperationBufferTests | Out-Null; if (-not $?) { throw "iteration $_ failed" } }
```

Expected: 100/100 pass without sleep-based synchronization.

- [ ] **Step 5: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Services/StartOperationBuffer.cs tests/Umamusume.CoreBridge.Tests/StartOperationBufferTests.cs
git commit -m "feat: add raw callback start buffer"
```

---

### Task 6: Service initialization and test-native seam

**Files:**
- Create: `src/Umamusume.CoreBridge/Services/IEventDispatcher.cs`
- Create: `src/Umamusume.CoreBridge/Services/IUmaService.cs`
- Create: `src/Umamusume.CoreBridge/Services/UmaService.cs`
- Create: `tests/Umamusume.CoreBridge.Tests/Fakes/FakeUmaNativeApi.cs`
- Create: `tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs`

**Interfaces:**
- Produces: fail-closed `InitializeAsync`, rooted native callback, typed event/diagnostic publication, and an injectable fake native API.
- Consumes: Tasks 3-5.

- [ ] **Step 1: Write initialization RED tests**

Test exact call order `SetUserDir → LoadResource → GetVersion → Create`. Add separate tests for invalid/non-absolute paths, each nonzero native return, empty version, null handle, double initialization, and operation calls before initialization. Verify failure never exposes a usable handle.

- [ ] **Step 2: Run RED**

Expected: compile failure because the service contracts and fake do not exist.

- [ ] **Step 3: Define service and dispatch contracts**

Use:

```csharp
public interface IEventDispatcher { void Post(Action action); }

public interface IUmaService : IAsyncDisposable
{
    string? CoreVersion { get; }
    event Action<ConnectionEvent>? ConnectionEventReceived;
    event Action<BridgeDiagnostic>? DiagnosticReceived;
    Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default);
    Task<ConnectionTerminalEvent> ConnectAsync(string adbPath, string serial, string profile, CancellationToken cancellationToken = default);
    Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default);
}
```

Make `ConnectionSucceededEvent` and `ConnectionFailedEvent` derive from `abstract record ConnectionTerminalEvent(ulong OperationId)`.

- [ ] **Step 4: Implement the scripted fake**

`FakeUmaNativeApi` records every method call, has configurable return values, captures `UmaApiCallback`, creates `SafeUmaHandle` with a destroy counter, and provides `Emit(int messageId, string json)`. Its S2 methods return `{0, InvalidArgument}` or `InvalidArgument` and never emit callbacks.

- [ ] **Step 5: Implement fail-closed initialization**

Canonicalize with `Path.GetFullPath`, require rooted existing base directory, create the app-data directory only after validation, then execute the exact native order. Store the callback in a readonly service field before `Create`. The callback synchronously checks `IntPtr.Zero`, copies with `Marshal.PtrToStringUTF8`, computes UTF-8 byte count, and converts accepted input into `RawCallback`; it catches every exception before returning to native code.

- [ ] **Step 6: Run GREEN and full tests**

Expected: all initialization scenarios pass, call order is exact, and no warning appears.

- [ ] **Step 7: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Services tests/Umamusume.CoreBridge.Tests/Fakes tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs
git commit -m "feat: add fail-closed managed bridge initialization"
```

---

### Task 7: Connect registration, routing, cancellation, and exactly-once completion

**Files:**
- Modify: `src/Umamusume.CoreBridge/Services/UmaService.cs`
- Modify: `tests/Umamusume.CoreBridge.Tests/Fakes/FakeUmaNativeApi.cs`
- Modify: `tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs`

**Interfaces:**
- Produces: complete `ConnectAsync` operation lifecycle and cancellation behavior.
- Consumes: parser, buffer, native seam, initialized handle.

- [ ] **Step 1: Write RED tests for pre-return and direct callbacks**

Configure the fake to invoke `ConnectionStarted`, progress, and success synchronously inside `Connect` before returning `{42,0}`. Assert ordered publication and one terminal Task result. Add the same scenario with callbacks emitted after return, plus deterministic concurrent callback/bind coverage.

- [ ] **Step 2: Write RED tests for contract failures**

Cover `{0,error}` with no callback, `{0,error}` with illegal buffered callback, `{0,0}`, `{42,error}`, a second start while one is active, null callback pointer, oversized copied JSON, wrong operation ID, malformed callback, progress before start, duplicate terminal, callback after terminal, and dispatcher failure. Illegal events produce diagnostics and never replace the terminal result.

- [ ] **Step 3: Write RED cancellation tests**

Cover token cancellation while native `Connect` is still on the stack, `CancelOperationAsync` after binding, both forms racing with terminal completion, a caller-supplied wrong operation ID, native cancel returning `InvalidArgument`, and disposal of the cancellation registration after terminal completion. Use barriers and explicit completion sources, not delays.

- [ ] **Step 4: Run RED**

Expected: tests fail because `ConnectAsync` has no operation coordinator.

- [ ] **Step 5: Implement the one-operation coordinator**

Before calling `_native.Connect`, install one `StartOperationBuffer` and one operation state under `_operationLock`, then release the lock. After the native return, validate the two-field invariant, create `TaskCompletionSource<ConnectionTerminalEvent>` with asynchronous continuations, bind the nonzero ID, register cancellation, replay, and route later callbacks through one method.

The router parses outside the native callback boundary, verifies the bound operation ID and event order, updates state under lock, disposes cancellation registration on the first terminal event, then completes/publishes outside the lock. A malformed callback attributable to the sole starting/active operation completes that operation with `ManagedBridgeException`; un-attributable and late callbacks are diagnostic-only.

For cancellation before binding, set a pending flag. After bind, invoke `_native.CancelOperation` exactly once outside the lock. Treat native success as accepted/idempotent; diagnose `InvalidArgument` unless terminal completion already won the race.

- [ ] **Step 6: Run GREEN, full tests, and 100 race repetitions**

Run filtered service tests, full managed tests, then repeat only the pre-return/concurrent/cancel race tests 100 times. Expected: every iteration passes with exactly-once completion.

- [ ] **Step 7: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Services/UmaService.cs tests/Umamusume.CoreBridge.Tests/Fakes/FakeUmaNativeApi.cs tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs
git commit -m "feat: add race-safe managed connection lifecycle"
```

---

### Task 8: Coordinated destruction and timeout containment

**Files:**
- Create: `src/Umamusume.CoreBridge/Services/AbandonedNativeHandleRegistry.cs`
- Modify: `src/Umamusume.CoreBridge/Services/UmaService.cs`
- Modify: `src/Umamusume.CoreBridge/Native/SafeUmaHandle.cs`
- Modify: `tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs`

**Interfaces:**
- Produces: bounded `DisposeAsync`, no double destroy, callback rooting through destruction, and intentional leak containment when the invariant fails.

- [ ] **Step 1: Write destruction RED tests**

Test idle dispose, active-operation cancellation then terminal then destroy, no new operation after disposal starts, destroy delegate invoked exactly once, callbacks ignored after disposal begins, and callback delegate retained until destroy returns.

- [ ] **Step 2: Write timeout RED tests**

Use an injected shutdown timeout through an internal constructor so tests use a short virtual duration without sleeps. Drive timeout with a controllable `TimeProvider`. Test both failure points: no terminal event before the bound, and blocking destroy not returning within the remaining bound.

- [ ] **Step 3: Run RED**

Expected: timeout and ownership tests fail because disposal is not coordinated.

- [ ] **Step 4: Implement bounded ownership transfer**

Set the disposing flag first, request cancellation, and await the terminal task with `Task.WaitAsync(timeout, timeProvider)`. If no terminal event arrives, call `SafeUmaHandle.Abandon()`, register the raw pointer plus rooted callback in `AbandonedNativeHandleRegistry`, emit one fatal diagnostic, and return without `UmaDestroy`.

If terminal arrives, run `handle.Dispose()` on a dedicated Task. If destroy exceeds the remaining 10-second budget, register the in-flight destroy Task and rooted callback until that Task completes; never call destroy again. If destroy completes, clear callback roots and service references. Registry entries remove themselves only after an in-flight destroy finishes; deliberately abandoned raw handles remain until process exit.

- [ ] **Step 5: Run GREEN and lifecycle regression tests**

Expected: all disposal cases pass without real-time sleeping; destroy count remains 0 for abandoned-before-destroy and 1 for normal/in-flight paths.

- [ ] **Step 6: Commit if explicitly authorized**

```powershell
git add src/Umamusume.CoreBridge/Services/AbandonedNativeHandleRegistry.cs src/Umamusume.CoreBridge/Services/UmaService.cs src/Umamusume.CoreBridge/Native/SafeUmaHandle.cs tests/Umamusume.CoreBridge.Tests/UmaServiceTests.cs
git commit -m "feat: add bounded native bridge shutdown"
```

---

### Task 9: Isolated native integration and unified acceptance gate

**Files:**
- Create: `tools/Umamusume.CoreBridge.IntegrationHost/Umamusume.CoreBridge.IntegrationHost.csproj`
- Create: `tools/Umamusume.CoreBridge.IntegrationHost/Program.cs`
- Create: `tests/Umamusume.CoreBridge.Tests/NativeLoadTests.cs`
- Create: `tests/Umamusume.CoreBridge.Tests/NativeFactAttribute.cs`
- Modify: `tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj`
- Modify: `tests/CMakeLists.txt`

**Interfaces:**
- Produces: process-isolated tests for process-global native initialization, DLL name loading, S2 stubs, and CTest integration.
- Consumes: built `$<TARGET_FILE:UmaAssistantCore>` and `resource/connection.json`.

- [ ] **Step 1: Write native integration RED tests**

Tests launch the IntegrationHost with scenarios `load`, `missing-resource`, `corrupt-resource`, `valid-resource`, `s2-stubs`, and `fake-adb-connect`. Assert exit codes and bounded JSON output. Each scenario runs in a fresh child process.

- [ ] **Step 2: Run RED without native staging**

Run `dotnet test` filtered to `NativeLoadTests`. Expected: FAIL because the host and staged DLL do not exist.

- [ ] **Step 3: Implement the IntegrationHost**

Create a `net10.0` executable referencing the bridge. `load` calls `NativeLibrary.TryLoad("UmamusumeCore.dll", out handle)` and frees only this probe handle. Resource scenarios build isolated temporary base directories. `valid-resource` initializes and disposes. `s2-stubs` initializes and calls all seven S2-facing operations, requiring synchronous `InvalidArgument` and no callback. `fake-adb-connect` receives the built `UmaFakeAdb` path and verifies the complete typed connection sequence.

- [ ] **Step 4: Stage native inputs from MSBuild properties**

Add required `NativeDllPath`, `FakeAdbPath`, and `ResourceFilePath` properties to the test command. Add a project reference to the IntegrationHost with `ReferenceOutputAssembly="false"`. Both the test and IntegrationHost projects use a `BeforeTargets="Build"` target that, when `NativeDllPath` is non-empty, validates all three inputs, copies the DLL and fake ADB beside that project's output, and copies `connection.json` to `resource/connection.json`. `NativeFactAttribute : FactAttribute` sets `Skip` in its constructor unless `UMA_NATIVE_INTEGRATION == "1"`, so unit-only `dotnet test` remains valid without staged native inputs.

- [ ] **Step 5: Add the managed CTest gate**

Append to `tests/CMakeLists.txt`:

```cmake
find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
add_test(
  NAME ManagedBridgeTests
  COMMAND "${CMAKE_COMMAND}" -E env UMA_NATIVE_INTEGRATION=1
    "${DOTNET_EXECUTABLE}" test
    "${CMAKE_SOURCE_DIR}/tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj"
    -c Release
    --property:NativeDllPath=$<TARGET_FILE:UmaAssistantCore>
    --property:FakeAdbPath=$<TARGET_FILE:UmaFakeAdb>
    --property:ResourceFilePath=${CMAKE_SOURCE_DIR}/resource/connection.json
  WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
)
```

The existing fake target is `UmaFakeAdb` (`tests/Connection/CMakeLists.txt:42-44`); keep the generator expression exactly as shown.

- [ ] **Step 6: Run the complete acceptance gate**

Run:

```powershell
cmake --preset release
cmake --build build/release --config Release
dotnet test tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj -c Release
ctest --test-dir build/release -C Release --output-on-failure
```

Expected: configure/build exit 0, all managed unit/integration tests pass, all existing native tests pass, and post-build output reports exactly 15 undecorated exports.

- [ ] **Step 7: Run forbidden-pattern and ABI checks**

Search managed source for `StringMarshalling.Utf8`, `DllImport`, implicit `string` parameters on raw imports, `Thread.Sleep`, `@ts-ignore`, `as any`, empty catches, and suppressed diagnostics. Expected: zero matches. Reflect imports and compare all 15 entry points and signatures against `UmaCaller.h`.

- [ ] **Step 8: Obtain independent review**

Provide the approved design, this plan, full diff, managed test output, CTest output, exact export output, and 100-run race evidence to Oracle. Require explicit `SPEC`, `QUALITY`, `CONCURRENCY`, and `LIFECYCLE` verdicts. Fix every Critical/Important finding through a new failing test and rerun all gates.

- [ ] **Step 9: Commit if explicitly authorized**

```powershell
git add tools/Umamusume.CoreBridge.IntegrationHost tests/Umamusume.CoreBridge.Tests/NativeLoadTests.cs tests/Umamusume.CoreBridge.Tests/Umamusume.CoreBridge.Tests.csproj tests/CMakeLists.txt
git commit -m "test: add managed bridge native acceptance gate"
```

---

## Completion Evidence

Phase 4 is complete only when the final report records:

- exact .NET SDK and package versions;
- managed test count with zero failures and warnings;
- 100/100 start-buffer/connect/cancel race passes;
- CMake Release configure and build exit 0;
- complete CTest count with zero failures;
- exactly 15 undecorated native exports;
- successful `NativeLibrary.TryLoad("UmamusumeCore.dll")` by name;
- missing/corrupt/valid resource scenarios in isolated processes;
- all S2 stubs returning synchronous `InvalidArgument` without callbacks;
- coordinated destroy and timeout-containment evidence;
- Oracle `SPEC`, `QUALITY`, `CONCURRENCY`, and `LIFECYCLE` approval.
