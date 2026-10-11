using System.Reflection;
using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge.Tests;

public sealed class InteropTests
{
    private static readonly string[] ExpectedExports =
    [
        "UmaCancelConnect",
        "UmaCancelOperation",
        "UmaCaptureAsync",
        "UmaConnectAsync",
        "UmaCopyFramePng",
        "UmaCreate",
        "UmaDestroy",
        "UmaGetFramePngSize",
        "UmaGetApiVersion",
        "UmaGetVersion",
        "UmaLoadResource",
        "UmaMatchTemplate",
        "UmaPrepareTouchAsync",
        "UmaKeyEventAsync",
        "UmaInputTextAsync",
        "UmaPackageOperationAsync",
        "UmaCompareImageChange",
        "UmaComparePersistentImageChange",
        "UmaRecognizeText",
        "UmaDecodeImageFile",
        "UmaDecodeImageBytes",
        "UmaCreateImageFromRgba",
        "UmaGetDecodedImageInfo",
        "UmaCopyDecodedImage",
        "UmaReleaseDecodedImage",
        "UmaLoadTemplateImage",
        "UmaRefreshResourceSnapshot",
        "UmaTransformImage",
        "UmaBinarizeColor",
        "UmaFindColorBlobs",
        "UmaCountColor",
        "UmaPrepareNumericGlyphs",
        "UmaComposeGrayImages",
        "UmaCountInkGroups",
        "UmaSampleImageDifference",
        "UmaSetInstanceOptions",
        "UmaReleaseFrame",
        "UmaSetUserDir",
        "UmaSwipeAsync",
        "UmaSwipeWithOptionsAsync",
        "UmaTouchAsync",
        "UmaTapAsync",
        "UmaVerifyGameAsync",
        "UmaValidatePipelineJson",
        "UmaRunPipelineAsync",
        "UmaRunGameTaskAsync",
        "UmaAppendTask",
        "UmaSetTaskParams",
        "UmaClearTasks",
        "UmaStartAsync",
        "UmaGetRunSnapshotJson",
        "UmaGetCatalogJson",
    ];

    [Fact]
    public void UmaStartResultMatchesWindowsX64Layout()
    {
        Assert.Equal(16, Marshal.SizeOf<UmaStartResult>());
        Assert.Equal(0, Marshal.OffsetOf<UmaStartResult>(nameof(UmaStartResult.OperationId)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<UmaStartResult>(nameof(UmaStartResult.ErrorCode)).ToInt32());
    }

    [Fact]
    public void UmaAppendTaskResultMatchesWindowsX64Layout()
    {
        Assert.Equal(16, Marshal.SizeOf<UmaAppendTaskResult>());
        Assert.Equal(0, Marshal.OffsetOf<UmaAppendTaskResult>(nameof(UmaAppendTaskResult.TaskId)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<UmaAppendTaskResult>(nameof(UmaAppendTaskResult.ErrorCode)).ToInt32());
    }

    [Fact]
    public void RecognitionAbiStructuresMatchWindowsX64Layout()
    {
        Assert.Equal(40, Marshal.SizeOf<UmaCoreBridgeNative.UmaImageViewNative>());
        Assert.Equal(80, Marshal.SizeOf<UmaCoreBridgeNative.UmaTemplateMatchOptionsNative>());
        Assert.Equal(32, Marshal.SizeOf<UmaCoreBridgeNative.UmaTemplateMatchResultNative>());
        Assert.Equal(32, Marshal.SizeOf<UmaCoreBridgeNative.UmaDecodedImageInfoNative>());
    }

    [Theory]
    [InlineData("General")]
    [InlineData("赛马娘-テスト")]
    public unsafe void Utf8PinnedRoundTripsAndIsNullTerminated(string value)
    {
        using var pinned = new UTF8Pinned(value);
        Assert.Equal(value, Marshal.PtrToStringUTF8((IntPtr)pinned.Pointer));
        Assert.Equal(0, pinned.Bytes[^1]);
    }

    [Fact]
    public void Utf8PinnedRejectsPointerAccessAfterDisposal()
    {
        var pinned = new UTF8Pinned("General");
        pinned.Dispose();

        Assert.Throws<ObjectDisposedException>(() => ReadPointer(pinned));
    }

    [Fact]
    public void CallbackDeclaresStdcall()
    {
        var attribute = typeof(UmaApiCallback).GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(CallingConvention.StdCall, attribute.CallingConvention);
    }

    [Fact]
    public void ErrorCodesAreCompleteAndDistinct()
    {
        int[] values = Enum.GetValues<ConnectionErrorCode>().Select(static value => (int)value).ToArray();
        Assert.Equal(Enumerable.Range(0, 18), values.Order());
    }

    [Fact]
    public void SafeUmaHandleCallsDestroyExactlyOnce()
    {
        var destroyCalls = 0;
        var handle = new SafeUmaHandle((IntPtr)42, _ => destroyCalls++);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, destroyCalls);
    }

    [Fact]
    public void NativeBridgeDeclaresExactlyTheExpectedExports()
    {
        string[] exports = typeof(UmaCoreBridgeNative)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Select(static method => method.GetCustomAttribute<LibraryImportAttribute>())
            .Where(static attribute => attribute is not null)
            .Select(static attribute => attribute!.EntryPoint)
            .OfType<string>()
            .Order()
            .ToArray();

        Assert.Equal(ExpectedExports.Order(), exports);
    }

    private static unsafe IntPtr ReadPointer(UTF8Pinned pinned) => (IntPtr)pinned.Pointer;
}
