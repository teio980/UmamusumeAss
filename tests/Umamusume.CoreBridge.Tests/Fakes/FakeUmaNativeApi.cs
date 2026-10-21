using System.Runtime.InteropServices;
using System.Text;

namespace Umamusume.CoreBridge.Tests.Fakes;

internal sealed class FakeUmaNativeApi : IUmaNativeApi
{
    private static int _nextNativeHandle = 41;
    private UmaApiCallback? _callback;

    internal List<string> Calls { get; } = [];
    internal int SetUserDirResult { get; set; }
    internal int LoadResourceResult { get; set; }
    internal int RefreshResourceSnapshotResult { get; set; }
    internal Action? BeforeLoadTemplateReturn { get; set; }
    internal RecognitionImage? TemplateImage { get; set; }
    internal int SetInstanceOptionsResult { get; set; }
    internal string? LastInstanceOptionsJson { get; private set; }
    internal string Version { get; set; } = "0.1.0";
    internal uint ApiVersion { get; set; } = 8;
    internal bool CreateInvalidHandle { get; set; }
    internal int DestroyCalls { get; private set; }
    internal ManualResetEventSlim? DestroyEntered { get; set; }
    internal ManualResetEventSlim? ContinueDestroy { get; set; }
    internal UmaStartResult ConnectResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult VerifyGameResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult CaptureResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult TapResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult SwipeResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult TouchResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult PrepareTouchResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult KeyEventResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult InputTextResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal UmaStartResult PackageOperationResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal int ValidatePipelineJsonResult { get; set; }
    internal string? ValidatePipelineErrorMessage { get; set; }
    internal string? LastValidatedPipelineJson { get; private set; }
    internal UmaStartResult RunPipelineResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal string? LastRunPipelineJson { get; private set; }
    internal string? LastRunPipelineEntryTask { get; private set; }
    internal Action? BeforeRunPipelineReturn { get; set; }
    internal UmaStartResult RunGameTaskResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal string? LastRunGameTaskId { get; private set; }
    internal string? LastRunGameTaskSettingsJson { get; private set; }
    internal Action? BeforeRunGameTaskReturn { get; set; }
    internal Action? BeforeVerifyGameReturn { get; set; }
    internal Action? BeforeCaptureReturn { get; set; }
    internal Action? BeforeTapReturn { get; set; }
    internal Action? BeforeSwipeReturn { get; set; }
    internal Action? BeforeTouchReturn { get; set; }
    internal Action? BeforePrepareTouchReturn { get; set; }
    internal Action? BeforeKeyEventReturn { get; set; }
    internal Action? BeforeInputTextReturn { get; set; }
    internal Action? BeforePackageOperationReturn { get; set; }
    internal byte[] FramePng { get; set; } = [137, 80, 78, 71, 13, 10, 26, 10, 0];
    internal int GetFramePngSizeResult { get; set; }
    internal int CopyFramePngResult { get; set; }
    internal bool ThrowOnCopyFramePng { get; set; }
    internal ManualResetEventSlim? CopyFrameEntered { get; set; }
    internal ManualResetEventSlim? ContinueCopyFrame { get; set; }
    internal List<ulong> ReleasedFrameIds { get; } = [];
    internal ulong LastTapFrameId { get; private set; }
    internal ulong LastSwipeFrameId { get; private set; }
    internal bool LastSwipeExtra { get; private set; }
    internal ulong LastTouchFrameId { get; private set; }
    internal int LastTouchPhase { get; private set; } = -1;
    internal ulong LastPreparedTouchFrameId { get; private set; }
    internal ulong LastKeyEventFrameId { get; private set; }
    internal string? LastKeyCode { get; private set; }
    internal ulong LastInputTextFrameId { get; private set; }
    internal string? LastInputText { get; private set; }
    internal string? LastPackageId { get; private set; }
    internal string? LastPackageActivity { get; private set; }
    internal PackageOperationKind? LastPackageOperation { get; private set; }
    internal Action? BeforeConnectReturn { get; set; }
    internal Action? BeforeLoadResourceReturn { get; set; }
    internal Action? BeforeCreateReturn { get; set; }
    internal Action<ulong>? CancelOperationAction { get; set; }
    internal int CancelOperationResult { get; set; }
    internal List<ulong> CanceledOperationIds { get; } = [];
    internal TemplateMatchResult MatchTemplateResult { get; set; } = new(false, 0, 0, 0, 0, 0);
    internal Action? BeforeMatchTemplateReturn { get; set; }
    internal TextRecognitionResult TextRecognitionResult { get; set; } = new([], "en-US", 0, 0);
    internal bool MeaningfulChangeResult { get; set; }
    internal bool PersistentChangeResult { get; set; }
    internal RecognitionImage? LastRecognitionScreen { get; private set; }
    internal RecognitionImage? LastRecognitionTemplate { get; private set; }
    internal TemplateMatchOptions? LastMatchOptions { get; private set; }
    internal RecognitionImage? LastRecognizedImage { get; private set; }
    internal string? LastOcrLanguage { get; private set; }

    public uint GetApiVersion()
    {
        Calls.Add(nameof(GetApiVersion));
        return ApiVersion;
    }

    public string GetVersion()
    {
        Calls.Add(nameof(GetVersion));
        return Version;
    }

    public SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg)
    {
        Calls.Add(nameof(Create));
        _callback = callback;
        IntPtr value = CreateInvalidHandle
            ? IntPtr.Zero
            : new IntPtr(Interlocked.Increment(ref _nextNativeHandle));
        var handle = new SafeUmaHandle(value, _ =>
        {
            DestroyCalls++;
            DestroyEntered?.Set();
            ContinueDestroy?.Wait();
        });
        BeforeCreateReturn?.Invoke();
        return handle;
    }

    public int SetUserDir(string path)
    {
        Calls.Add(nameof(SetUserDir));
        return SetUserDirResult;
    }

    public int LoadResource(string path)
    {
        Calls.Add(nameof(LoadResource));
        BeforeLoadResourceReturn?.Invoke();
        return LoadResourceResult;
    }

    public int SetInstanceOptions(SafeUmaHandle handle, string utf8Json)
    {
        Calls.Add(nameof(SetInstanceOptions));
        LastInstanceOptionsJson = utf8Json;
        return SetInstanceOptionsResult;
    }

    public int RefreshResourceSnapshot(SafeUmaHandle handle)
    {
        Calls.Add(nameof(RefreshResourceSnapshot));
        return RefreshResourceSnapshotResult;
    }

    public RecognitionImage? LoadTemplateImage(SafeUmaHandle handle, string path, TemplatePreparation preparation)
    {
        Calls.Add(nameof(LoadTemplateImage));
        BeforeLoadTemplateReturn?.Invoke();
        return TemplateImage;
    }

    public UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile)
    {
        Calls.Add(nameof(Connect));
        BeforeConnectReturn?.Invoke();
        return ConnectResult;
    }

    public int CancelConnect(SafeUmaHandle handle, ulong operationId) =>
        (int)ConnectionErrorCode.InvalidArgument;

    public int CancelOperation(SafeUmaHandle handle, ulong operationId)
    {
        Calls.Add(nameof(CancelOperation));
        CanceledOperationIds.Add(operationId);
        CancelOperationAction?.Invoke(operationId);
        return CancelOperationResult;
    }

    public UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId)
    {
        Calls.Add(nameof(VerifyGame));
        Calls.Add(packageId);
        BeforeVerifyGameReturn?.Invoke();
        return VerifyGameResult;
    }

    public UmaStartResult Capture(SafeUmaHandle handle)
    {
        Calls.Add(nameof(Capture));
        BeforeCaptureReturn?.Invoke();
        return CaptureResult;
    }

    public int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size)
    {
        size = (ulong)FramePng.Length;
        return GetFramePngSizeResult;
    }

    public int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination)
    {
        CopyFrameEntered?.Set();
        ContinueCopyFrame?.Wait();
        if (ThrowOnCopyFramePng)
        {
            throw new InvalidOperationException("Synthetic frame copy failure.");
        }

        if (CopyFramePngResult != (int)ConnectionErrorCode.Success)
        {
            return CopyFramePngResult;
        }

        if (destination.Length != FramePng.Length)
        {
            return (int)ConnectionErrorCode.InvalidArgument;
        }

        FramePng.CopyTo(destination);
        return (int)ConnectionErrorCode.Success;
    }

    public int ReleaseFrame(SafeUmaHandle handle, ulong frameId)
    {
        ReleasedFrameIds.Add(frameId);
        return (int)ConnectionErrorCode.Success;
    }

    public UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y)
    {
        Calls.Add(nameof(Tap));
        LastTapFrameId = frameId;
        BeforeTapReturn?.Invoke();
        return TapResult;
    }

    public UmaStartResult Swipe(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs,
        bool extraSwipe)
    {
        Calls.Add(nameof(Swipe));
        LastSwipeFrameId = frameId;
        LastSwipeExtra = extraSwipe;
        BeforeSwipeReturn?.Invoke();
        return SwipeResult;
    }

    public UmaStartResult Touch(SafeUmaHandle handle, ulong frameId, int x, int y, int phase)
    {
        Calls.Add(nameof(Touch));
        LastTouchFrameId = frameId;
        LastTouchPhase = phase;
        BeforeTouchReturn?.Invoke();
        return TouchResult;
    }

    public UmaStartResult PrepareTouch(SafeUmaHandle handle, ulong frameId)
    {
        Calls.Add(nameof(PrepareTouch));
        LastPreparedTouchFrameId = frameId;
        BeforePrepareTouchReturn?.Invoke();
        return PrepareTouchResult;
    }

    public UmaStartResult KeyEvent(SafeUmaHandle handle, ulong frameId, string keyCode)
    {
        Calls.Add(nameof(KeyEvent));
        LastKeyEventFrameId = frameId;
        LastKeyCode = keyCode;
        BeforeKeyEventReturn?.Invoke();
        return KeyEventResult;
    }

    public UmaStartResult InputText(SafeUmaHandle handle, ulong frameId, string text)
    {
        Calls.Add(nameof(InputText));
        LastInputTextFrameId = frameId;
        LastInputText = text;
        BeforeInputTextReturn?.Invoke();
        return InputTextResult;
    }

    public UmaStartResult PackageOperation(
        SafeUmaHandle handle,
        string packageId,
        string? activity,
        PackageOperationKind operation)
    {
        Calls.Add(nameof(PackageOperation));
        LastPackageId = packageId;
        LastPackageActivity = activity;
        LastPackageOperation = operation;
        BeforePackageOperationReturn?.Invoke();
        return PackageOperationResult;
    }

    public TemplateMatchResult MatchTemplate(
        SafeUmaHandle handle,
        RecognitionImage screen,
        RecognitionImage templateImage,
        TemplateMatchOptions options)
    {
        Calls.Add(nameof(MatchTemplate));
        LastRecognitionScreen = screen;
        LastRecognitionTemplate = templateImage;
        LastMatchOptions = options;
        BeforeMatchTemplateReturn?.Invoke();
        return MatchTemplateResult;
    }

    public TextRecognitionResult RecognizeText(
        SafeUmaHandle handle,
        RecognitionImage image,
        string language)
    {
        Calls.Add(nameof(RecognizeText));
        LastRecognizedImage = image;
        LastOcrLanguage = language;
        return TextRecognitionResult;
    }

    public bool HasMeaningfulChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage after,
        TemplateMatchResult match)
    {
        Calls.Add(nameof(HasMeaningfulChange));
        return MeaningfulChangeResult;
    }

    public bool HasPersistentChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage firstAfter,
        RecognitionImage settledAfter,
        TemplateMatchResult match)
    {
        Calls.Add(nameof(HasPersistentChange));
        return PersistentChangeResult;
    }

    public int ValidatePipelineJson(string json, out string? errorMessage)
    {
        Calls.Add(nameof(ValidatePipelineJson));
        LastValidatedPipelineJson = json;
        errorMessage = ValidatePipelineErrorMessage;
        return ValidatePipelineJsonResult;
    }

    public UmaStartResult RunPipelineAsync(SafeUmaHandle handle, string pipelineJson, string? entryTask)
    {
        Calls.Add(nameof(RunPipelineAsync));
        LastRunPipelineJson = pipelineJson;
        LastRunPipelineEntryTask = entryTask;
        BeforeRunPipelineReturn?.Invoke();
        return RunPipelineResult;
    }

    public UmaStartResult RunGameTaskAsync(SafeUmaHandle handle, string taskId, string settingsJson)
    {
        Calls.Add(nameof(RunGameTaskAsync));
        LastRunGameTaskId = taskId;
        LastRunGameTaskSettingsJson = settingsJson;
        BeforeRunGameTaskReturn?.Invoke();
        return RunGameTaskResult;
    }

    internal UmaAppendTaskResult AppendTaskResult { get; set; } = new(1, 0);
    internal List<UmaAppendTaskResult> AppendTaskResults { get; } = [];
    internal List<(string TaskId, string SettingsJson)> AppendedTasks { get; } = [];
    internal int SetTaskParamsResult { get; set; }
    internal int ClearTasksResult { get; set; }
    internal UmaStartResult StartQueueResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);
    internal string RunSnapshotJson { get; set; } = "{}";
    internal Func<int, string>? RunSnapshotJsonForCall { get; set; }
    internal ulong? RunSnapshotRequiredLengthOverride { get; set; }
    internal int RunSnapshotJsonCalls { get; private set; }
    internal string CatalogJson { get; set; } = "{}";
    internal Func<int, string>? CatalogJsonForCall { get; set; }
    internal int CatalogJsonCalls { get; private set; }
    internal Action? BeforeStartQueueReturn { get; set; }

    public UmaAppendTaskResult AppendTask(SafeUmaHandle handle, string taskId, string settingsJson)
    {
        Calls.Add(nameof(AppendTask));
        int index = AppendedTasks.Count;
        AppendedTasks.Add((taskId, settingsJson));
        return index < AppendTaskResults.Count
            ? AppendTaskResults[index]
            : new UmaAppendTaskResult(
                AppendTaskResult.TaskId + (ulong)index,
                AppendTaskResult.ErrorCode);
    }

    public int SetTaskParams(SafeUmaHandle handle, ulong taskId, string settingsJson)
    {
        Calls.Add(nameof(SetTaskParams));
        return SetTaskParamsResult;
    }

    public int ClearTasks(SafeUmaHandle handle)
    {
        Calls.Add(nameof(ClearTasks));
        return ClearTasksResult;
    }

    public UmaStartResult StartQueueAsync(SafeUmaHandle handle)
    {
        Calls.Add(nameof(StartQueueAsync));
        BeforeStartQueueReturn?.Invoke();
        return StartQueueResult;
    }

    public int GetRunSnapshotJson(SafeUmaHandle handle, byte[]? destination, ulong capacity, out ulong requiredLength)
    {
        Calls.Add(nameof(GetRunSnapshotJson));
        int index = RunSnapshotJsonCalls++;
        string json = RunSnapshotJsonForCall?.Invoke(index) ?? RunSnapshotJson;
        byte[] bytes = Encoding.UTF8.GetBytes(json + '\0');
        requiredLength = RunSnapshotRequiredLengthOverride ?? (ulong)bytes.Length;
        if (destination is null || capacity < requiredLength)
            return (int)ConnectionErrorCode.BufferTooSmall;
        bytes.CopyTo(destination, 0);
        return 0;
    }

    public int GetCatalogJson(SafeUmaHandle handle, string catalogType, byte[]? destination, ulong capacity, out ulong requiredLength)
    {
        Calls.Add(nameof(GetCatalogJson));
        int index = CatalogJsonCalls++;
        string json = CatalogJsonForCall?.Invoke(index) ?? CatalogJson;
        byte[] bytes = Encoding.UTF8.GetBytes(json + '\0');
        requiredLength = (ulong)bytes.Length;
        if (destination is null || capacity < requiredLength)
            return (int)ConnectionErrorCode.BufferTooSmall;
        bytes.CopyTo(destination, 0);
        return 0;
    }

    internal unsafe void Emit(int messageId, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + '\0');
        fixed (byte* pointer = bytes)
        {
            _callback?.Invoke(messageId, (IntPtr)pointer, IntPtr.Zero);
        }
    }

    internal void EmitNull(int messageId) =>
        _callback?.Invoke(messageId, IntPtr.Zero, IntPtr.Zero);
}
