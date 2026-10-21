namespace Umamusume.CoreBridge;

internal interface IUmaNativeApi
{
    uint GetApiVersion();
    string GetVersion();
    SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg);
    int SetUserDir(string path);
    int LoadResource(string path);
    int RefreshResourceSnapshot(SafeUmaHandle handle);
    RecognitionImage? LoadTemplateImage(SafeUmaHandle handle, string path, TemplatePreparation preparation);
    int SetInstanceOptions(SafeUmaHandle handle, string utf8Json);
    UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile);
    int CancelConnect(SafeUmaHandle handle, ulong operationId);
    int CancelOperation(SafeUmaHandle handle, ulong operationId);
    UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId);
    UmaStartResult Capture(SafeUmaHandle handle);
    int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size);
    int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination);
    int ReleaseFrame(SafeUmaHandle handle, ulong frameId);
    UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y);
    UmaStartResult Swipe(SafeUmaHandle handle, ulong frameId, int x1, int y1, int x2, int y2, int durationMs, bool extraSwipe);
    UmaStartResult Touch(SafeUmaHandle handle, ulong frameId, int x, int y, int phase);
    UmaStartResult PrepareTouch(SafeUmaHandle handle, ulong frameId);
    UmaStartResult KeyEvent(SafeUmaHandle handle, ulong frameId, string keyCode);
    UmaStartResult InputText(SafeUmaHandle handle, ulong frameId, string text);
    UmaStartResult PackageOperation(
        SafeUmaHandle handle,
        string packageId,
        string? activity,
        PackageOperationKind operation);
    TemplateMatchResult MatchTemplate(
        SafeUmaHandle handle,
        RecognitionImage screen,
        RecognitionImage templateImage,
        TemplateMatchOptions options);
    TextRecognitionResult RecognizeText(
        SafeUmaHandle handle,
        RecognitionImage image,
        string language);
    bool HasMeaningfulChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage after,
        TemplateMatchResult match);
    bool HasPersistentChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage firstAfter,
        RecognitionImage settledAfter,
        TemplateMatchResult match);
    int ValidatePipelineJson(string json, out string? errorMessage);
    UmaStartResult RunPipelineAsync(SafeUmaHandle handle, string pipelineJson, string? entryTask);
    UmaStartResult RunGameTaskAsync(SafeUmaHandle handle, string taskId, string settingsJson);
    UmaAppendTaskResult AppendTask(SafeUmaHandle handle, string taskId, string settingsJson);
    int SetTaskParams(SafeUmaHandle handle, ulong taskId, string settingsJson);
    int ClearTasks(SafeUmaHandle handle);
    UmaStartResult StartQueueAsync(SafeUmaHandle handle);
    int GetRunSnapshotJson(SafeUmaHandle handle, byte[]? destination, ulong capacity, out ulong requiredLength);
    int GetCatalogJson(SafeUmaHandle handle, string catalogType, byte[]? destination, ulong capacity, out ulong requiredLength);
}
