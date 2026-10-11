using Umamusume.CoreBridge.Tests.Fakes;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Umamusume.CoreBridge.Tests;

public sealed class UmaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"uma-bridge-{Guid.NewGuid():N}");

    public UmaServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task InitializeCallsNativeApiInRequiredOrder()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string appData = Path.Combine(_root, "app-data");

        await service.InitializeAsync(_root, appData);

        Assert.Equal(["GetApiVersion", "SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
        Assert.Equal("0.1.0", service.CoreVersion);
        Assert.Equal(8u, service.CoreApiVersion);
        Assert.Equal(Path.Combine(_root, "resource"), service.ResourcePath);
        Assert.True(Directory.Exists(appData));
    }

    [Theory]
    [InlineData("relative", true)]
    [InlineData("relative", false)]
    public async Task InitializeRejectsRelativePaths(string path, bool useAsBase)
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        string basePath = useAsBase ? path : _root;
        string appData = useAsBase ? Path.Combine(_root, "data") : path;

        await Assert.ThrowsAsync<ArgumentException>(() => service.InitializeAsync(basePath, appData));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsMissingBaseDirectory()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.InitializeAsync(Path.Combine(_root, "missing"), Path.Combine(_root, "data")));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task InitializeStopsWhenSetUserDirFails()
    {
        var native = new FakeUmaNativeApi { SetUserDirResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["GetApiVersion", "SetUserDir"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeStopsWhenLoadResourceFails()
    {
        var native = new FakeUmaNativeApi { LoadResourceResult = 11 };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["GetApiVersion", "SetUserDir", "LoadResource"], native.Calls);
        Assert.Null(service.CoreVersion);
    }

    [Fact]
    public async Task InitializeRejectsEmptyVersion()
    {
        var native = new FakeUmaNativeApi { Version = "" };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["GetApiVersion", "SetUserDir", "LoadResource", "GetVersion"], native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsInvalidHandle()
    {
        var native = new FakeUmaNativeApi { CreateInvalidHandle = true };
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));
        Assert.Equal(["GetApiVersion", "SetUserDir", "LoadResource", "GetVersion", "Create"], native.Calls);
    }

    [Fact]
    public async Task InitializeRejectsUnsupportedNativeApiVersionBeforeUsingOtherExports()
    {
        var native = new FakeUmaNativeApi { ApiVersion = 12 };
        await using var service = CreateService(native);

        var exception = await Assert.ThrowsAsync<ManagedBridgeException>(() => Initialize(service));

        Assert.Contains("native core API version 12", exception.Message);
        Assert.Contains("expected API 8, 9, 10, or 11", exception.Message);
        Assert.Equal(["GetApiVersion"], native.Calls);
        Assert.Null(service.CoreVersion);
        Assert.Null(service.CoreApiVersion);
    }

    [Fact]
    public async Task RecognitionCallsNativeMatcherAndPreservesImagePlanesAndOptions()
    {
        var expected = new TemplateMatchResult(true, 0.987654321, 12, 34, 56, 78);
        var native = new FakeUmaNativeApi { MatchTemplateResult = expected };
        await using var service = CreateService(native);
        await Initialize(service);
        var screen = new RecognitionImage(1, 1, [12], [1, 2, 3, 255]);
        var template = new RecognitionImage(1, 1, [34], [4, 5, 6, 255]);
        var options = new TemplateMatchOptions(
            TemplateMatchMethod.ScaledMasked, [1, 2, 3, 4], .88, 900, 1600,
            Scales: [.8, 1.0, 1.2]);

        TemplateMatchResult actual = service.MatchTemplate(screen, template, options);

        Assert.Equal(expected, actual);
        Assert.Same(screen, native.LastRecognitionScreen);
        Assert.Same(template, native.LastRecognitionTemplate);
        Assert.Same(options, native.LastMatchOptions);
        Assert.Contains(nameof(FakeUmaNativeApi.MatchTemplate), native.Calls);
    }

    [Fact]
    public async Task RecognitionReturnsNativeOcrAndImageChangeResults()
    {
        var expectedOcr = new TextRecognitionResult(
            [new TextDetection("Race!", new TextRecognitionRect(10, 20, 30, 12))],
            "en-US", 320, 180);
        var native = new FakeUmaNativeApi
        {
            TextRecognitionResult = expectedOcr,
            MeaningfulChangeResult = true,
            PersistentChangeResult = true,
        };
        await using var service = CreateService(native);
        await Initialize(service);
        var before = new RecognitionImage(1, 1, [0], [0, 0, 0, 255]);
        var after = new RecognitionImage(1, 1, [1], [1, 1, 1, 255]);
        var match = new TemplateMatchResult(true, .9, 0, 0, 1, 1);

        Assert.Equal(expectedOcr, service.RecognizeText(before, "en-US"));
        Assert.True(service.HasMeaningfulChange(before, after, match));
        Assert.True(service.HasPersistentChange(before, after, after, match));
        Assert.Same(before, native.LastRecognizedImage);
        Assert.Equal("en-US", native.LastOcrLanguage);
        Assert.Contains(nameof(FakeUmaNativeApi.RecognizeText), native.Calls);
        Assert.Contains(nameof(FakeUmaNativeApi.HasMeaningfulChange), native.Calls);
        Assert.Contains(nameof(FakeUmaNativeApi.HasPersistentChange), native.Calls);
    }

    [Fact]
    public async Task DisposeWaitsForRecognitionLeaseBeforeReleasingNativeHandle()
    {
        using var recognitionEntered = new ManualResetEventSlim();
        using var continueRecognition = new ManualResetEventSlim();
        var expected = new TemplateMatchResult(true, .9, 3, 4, 5, 6);
        var native = new FakeUmaNativeApi
        {
            MatchTemplateResult = expected,
            BeforeMatchTemplateReturn = () =>
            {
                recognitionEntered.Set();
                continueRecognition.Wait();
            },
        };
        var service = CreateService(native);
        await Initialize(service);
        var image = new RecognitionImage(1, 1, [0]);

        Task<TemplateMatchResult> recognition = Task.Run(() => service.MatchTemplate(
            image, image, new TemplateMatchOptions(TemplateMatchMethod.Gray, null, .8, 1, 1)));
        Task disposal = Task.CompletedTask;
        try
        {
            Assert.True(recognitionEntered.Wait(TimeSpan.FromSeconds(5)));

            disposal = service.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, native.DestroyCalls);
        }
        finally
        {
            continueRecognition.Set();
        }

        Assert.Equal(expected, await recognition.WaitAsync(TimeSpan.FromSeconds(5)));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task DisposeTimeoutDuringRecognitionRetainsHandleAndCallbackRoot()
    {
        using var recognitionEntered = new ManualResetEventSlim();
        using var continueRecognition = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            BeforeMatchTemplateReturn = () =>
            {
                recognitionEntered.Set();
                continueRecognition.Wait();
            },
        };
        var service = CreateService(native, TimeSpan.Zero);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        var callback = (UmaApiCallback)typeof(UmaService)
            .GetField("_nativeCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var image = new RecognitionImage(1, 1, [0]);
        Task<TemplateMatchResult> recognition = Task.Run(() => service.MatchTemplate(
            image, image, new TemplateMatchOptions(TemplateMatchMethod.Gray, null, .8, 1, 1)));
        try
        {
            Assert.True(recognitionEntered.Wait(TimeSpan.FromSeconds(5)));

            await service.DisposeAsync();

            Assert.Equal(0, native.DestroyCalls);
            Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.FatalShutdownTimeout);
            var abandoned = (ConcurrentDictionary<IntPtr, UmaApiCallback>)typeof(AbandonedNativeHandleRegistry)
                .GetField("Abandoned", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            Assert.Contains(abandoned.Values, retained => ReferenceEquals(retained, callback));
        }
        finally
        {
            continueRecognition.Set();
        }

        await recognition.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InstanceOptionsAreSerializedAndCannotChangeWhileAnOperationStarts()
    {
        using var connectEntered = new ManualResetEventSlim();
        using var continueConnect = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            BeforeConnectReturn = () =>
            {
                connectEntered.Set();
                continueConnect.Wait();
            },
        };
        await using var service = CreateService(native);
        await Initialize(service);

        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(
            7, "8070450532247928839", ScreenshotCaptureMethod.Raw));
        Assert.Contains(nameof(FakeUmaNativeApi.SetInstanceOptions), native.Calls);
        using (JsonDocument options = JsonDocument.Parse(native.LastInstanceOptionsJson!))
        {
            Assert.Equal(1, options.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(7, options.RootElement.GetProperty("display_id").GetInt32());
            Assert.Equal("8070450532247928839", options.RootElement.GetProperty("surface_display_id").GetString());
            Assert.Equal("raw", options.RootElement.GetProperty("screenshot_method").GetString());
            Assert.Equal(10.0, options.RootElement.GetProperty("swipe_duration_multiplier").GetDouble());
            Assert.Equal(100, options.RootElement.GetProperty("extra_swipe_distance").GetInt32());
            Assert.Equal(500, options.RootElement.GetProperty("extra_swipe_duration_ms").GetInt32());
        }

        Task<ConnectionTerminalEvent> connect = Task.Run(
            () => service.ConnectAsync("adb.exe", "serial", "General"));
        Assert.True(connectEntered.Wait(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.SetInstanceOptionsAsync(new DeviceInstanceOptions()));
        continueConnect.Set();
        await Assert.ThrowsAsync<ManagedBridgeException>(() => connect);
        Assert.Equal(1, native.Calls.Count(call => call == nameof(FakeUmaNativeApi.SetInstanceOptions)));
    }

    [Theory]
    [InlineData("/data/local/tmp/")]
    [InlineData("/data/local/tmp/.")]
    [InlineData("/data/local/tmp/sub//helper")]
    [InlineData("/data/local/tmp/../helper")]
    public async Task InstanceOptionsRejectTouchRemoteDirectoriesAndTraversal(string remotePath)
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetInstanceOptionsAsync(
            new DeviceInstanceOptions(
                TouchBackend: TouchInputBackend.Minitouch,
                TouchBinaryPath: "minitouch",
                TouchRemotePath: remotePath)));

        Assert.DoesNotContain(nameof(FakeUmaNativeApi.SetInstanceOptions), native.Calls);
    }

    [Theory]
    [InlineData(TouchInputBackend.Minitouch)]
    [InlineData(TouchInputBackend.MaaTouch)]
    public async Task InstanceOptionsRequireLocalTouchBinaryBeforeNativeUpdate(TouchInputBackend backend)
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetInstanceOptionsAsync(
            new DeviceInstanceOptions(TouchBackend: backend, TouchBinaryPath: " ")));

        Assert.DoesNotContain(nameof(FakeUmaNativeApi.SetInstanceOptions), native.Calls);
    }

    [Fact]
    public async Task SwipeAsyncCarriesTheLegacyExtraSwipeFlagThroughTheNativeApi()
    {
        var native = new FakeUmaNativeApi
        {
            CaptureResult = new UmaStartResult(71, 0),
            SwipeResult = new UmaStartResult(72, 0),
        };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(17, (ulong)native.FramePng.Length)));
        await using var service = CreateService(native);
        await Initialize(service);

        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(captured.FrameLease);
        native.BeforeSwipeReturn = () => native.Emit(8,
            DeviceEnvelope(72, 1, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));

        var result = await service.SwipeAsync(frame, 1, 2, 3, 4, 50, extraSwipe: true);

        Assert.IsType<InputSucceededEvent>(result);
        Assert.True(native.LastSwipeExtra);
        frame.Dispose();
    }

    [Fact]
    public async Task KeyAndTextInputForwardUtf8AndTheExactSourceFrame()
    {
        var native = new FakeUmaNativeApi
        {
            CaptureResult = new UmaStartResult(71, 0),
            KeyEventResult = new UmaStartResult(72, 0),
            InputTextResult = new UmaStartResult(73, 0),
        };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(17, (ulong)native.FramePng.Length)));
        await using var service = CreateService(native);
        await Initialize(service);

        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(captured.FrameLease);
        native.BeforeKeyEventReturn = () => native.Emit(8,
            DeviceEnvelope(72, 2, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));
        native.BeforeInputTextReturn = () => native.Emit(8,
            DeviceEnvelope(73, 3, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));

        var keyResult = Assert.IsType<InputSucceededEvent>(
            await service.KeyEventAsync(frame, "KEYCODE_BACK"));
        string unicodeText = "1,500,000 CC 马娘";
        var textResult = Assert.IsType<InputSucceededEvent>(
            await service.InputTextAsync(frame, unicodeText));

        Assert.Equal(frame.FrameId, native.LastKeyEventFrameId);
        Assert.Equal("KEYCODE_BACK", native.LastKeyCode);
        Assert.Equal(frame.FrameId, keyResult.SourceFrame.FrameId);
        Assert.Equal(frame.FrameId, native.LastInputTextFrameId);
        Assert.Equal(unicodeText, native.LastInputText);
        Assert.Equal(frame.FrameId, textResult.SourceFrame.FrameId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.InputTextAsync(frame, ""));
        frame.Dispose();
    }

    [Fact]
    public async Task PackageOperationForwardsArgumentsAndParsesItsCompletion()
    {
        var native = new FakeUmaNativeApi
        {
            PackageOperationResult = new UmaStartResult(74, 0),
        };
        native.BeforePackageOperationReturn = () => native.Emit(13,
            DeviceEnvelope(74, 1, "PackageOperationCompleted",
                new
                {
                    operation = 1,
                    command = new
                    {
                        started = true,
                        exit_code = 0,
                        timed_out = false,
                        canceled = false,
                        error_code = 0,
                        error_message = "",
                        stdout = "Starting",
                        stderr = "",
                    },
                    process_running = true,
                }));
        await using var service = CreateService(native);
        await Initialize(service);

        var result = Assert.IsType<PackageOperationCompletedEvent>(await service.PackageOperationAsync(
            "com.example.game", ".MainActivity", PackageOperationKind.StartActivity));

        Assert.Equal("com.example.game", native.LastPackageId);
        Assert.Equal(".MainActivity", native.LastPackageActivity);
        Assert.Equal(PackageOperationKind.StartActivity, native.LastPackageOperation);
        Assert.Equal("Starting", result.Command.StandardOutput);
        Assert.True(result.ProcessRunning);
    }

    [Fact]
    public async Task KeyAndTextInputCannotStartAlongsideAnActiveDeviceOperation()
    {
        using var keyEntered = new ManualResetEventSlim();
        using var continueKey = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            CaptureResult = new UmaStartResult(71, 0),
            KeyEventResult = new UmaStartResult(72, 0),
        };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(17, (ulong)native.FramePng.Length)));
        native.BeforeKeyEventReturn = () =>
        {
            keyEntered.Set();
            continueKey.Wait();
            native.Emit(8,
                DeviceEnvelope(72, 2, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(captured.FrameLease);

        try
        {
            Task<DeviceOperationEvent> active = Task.Run(() => service.KeyEventAsync(frame, "4"));
            Assert.True(keyEntered.Wait(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAsync<ManagedBridgeException>(() => service.InputTextAsync(frame, "concurrent"));
            continueKey.Set();
            Assert.IsType<InputSucceededEvent>(await active);
        }
        finally
        {
            continueKey.Set();
            frame.Dispose();
        }
    }

    [Fact]
    public async Task HeldTouchCanCaptureWhilePressedAndReleaseWithTheMatchingPressFrame()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);
        native.CaptureResult = new UmaStartResult(71, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(17, (ulong)native.FramePng.Length)));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(captured.FrameLease);
        native.PrepareTouchResult = new UmaStartResult(72, 0);
        native.BeforePrepareTouchReturn = () => native.Emit(8,
            DeviceEnvelope(72, 1, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));
        Assert.IsType<InputSucceededEvent>(await service.PrepareTouchAsync(frame));
        Assert.Equal(frame.FrameId, native.LastPreparedTouchFrameId);

        native.TouchResult = new UmaStartResult(73, 0);
        native.BeforeTouchReturn = () => native.Emit(8,
            DeviceEnvelope(73, 1, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));
        Assert.IsType<InputSucceededEvent>(await service.TouchDownAsync(frame, 540, 960));
        Assert.Equal(frame.FrameId, native.LastTouchFrameId);
        Assert.Equal(0, native.LastTouchPhase);

        native.CaptureResult = new UmaStartResult(74, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(74, 1, "FrameCaptured", FrameMetadata(18, (ulong)native.FramePng.Length)));
        var duringHold = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        Assert.Equal(18UL, duringHold.Frame.FrameId);

        native.TouchResult = new UmaStartResult(75, 0);
        native.BeforeTouchReturn = () => native.Emit(8,
            DeviceEnvelope(75, 1, "InputSucceeded", InputPayload(17, (ulong)native.FramePng.Length)));
        Assert.IsType<InputSucceededEvent>(await service.TouchUpAsync(frame));
        Assert.Equal(frame.FrameId, native.LastTouchFrameId);
        Assert.Equal(1, native.LastTouchPhase);
        frame.Dispose();
        duringHold.FrameLease!.Dispose();
    }

    [Fact]
    public async Task DisplayMappingChangesDetachOldFrameLeasesWithoutReleasingWrongNativeFrames()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);
        // Changing only the screenshot route retains a lease; changing the
        // logical/SurfaceFlinger mapping detaches leases invalidated by native.
        native.CaptureResult = new UmaStartResult(71, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(44, (ulong)native.FramePng.Length)));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        Assert.NotNull(captured.FrameLease);
        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(
            ScreenshotMethod: ScreenshotCaptureMethod.Raw));
        Assert.False(captured.FrameLease!.IsDisposed);
        captured.FrameLease.Dispose();
        Assert.Contains(44UL, native.ReleasedFrameIds);

        native.CaptureResult = new UmaStartResult(72, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(72, 2, "FrameCaptured", FrameMetadata(45, (ulong)native.FramePng.Length)));
        var second = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        Assert.NotNull(second.FrameLease);
        int releasesBeforeChange = native.ReleasedFrameIds.Count;
        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(7, "7"));
        Assert.True(second.FrameLease!.IsDisposed);
        second.FrameLease.Dispose();
        Assert.Equal(releasesBeforeChange, native.ReleasedFrameIds.Count);
    }

    [Fact]
    public async Task EquivalentSurfaceDisplayIdsAreCanonicalizedWithoutInvalidatingFrameLeases()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(7, "00012"));
        using (JsonDocument options = JsonDocument.Parse(native.LastInstanceOptionsJson!))
        {
            Assert.Equal("12", options.RootElement.GetProperty("surface_display_id").GetString());
        }

        native.CaptureResult = new UmaStartResult(90, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(90, 1, "FrameCaptured", FrameMetadata(90, (ulong)native.FramePng.Length)));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame lease = Assert.IsType<DeviceFrame>(captured.FrameLease);

        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(7, "12"));
        Assert.False(lease.IsDisposed);
        using (JsonDocument options = JsonDocument.Parse(native.LastInstanceOptionsJson!))
        {
            Assert.Equal("12", options.RootElement.GetProperty("surface_display_id").GetString());
        }

        lease.Dispose();
        Assert.Contains(90UL, native.ReleasedFrameIds);

        native.CaptureResult = new UmaStartResult(91, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(91, 2, "FrameCaptured", FrameMetadata(91, (ulong)native.FramePng.Length)));
        var changedMappingFrame = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        int releasesBeforeMappingChange = native.ReleasedFrameIds.Count;
        await service.SetInstanceOptionsAsync(new DeviceInstanceOptions(7, "13"));
        Assert.True(changedMappingFrame.FrameLease!.IsDisposed);
        changedMappingFrame.FrameLease.Dispose();
        Assert.Equal(releasesBeforeMappingChange, native.ReleasedFrameIds.Count);
    }

    [Fact]
    public async Task ResourceReloadHoldsTheSchedulingGateUntilNativeSnapshotSwapCompletes()
    {
        using var loadEntered = new ManualResetEventSlim();
        using var continueLoad = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);
        native.Calls.Clear();
        native.BeforeLoadResourceReturn = () =>
        {
            loadEntered.Set();
            continueLoad.Wait();
        };
        native.BeforeConnectReturn = () => { };

        Task reload = Task.Run(() => service.ReloadResourceAsync(_root));
        Assert.True(loadEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<ConnectionTerminalEvent> connect = Task.Run(
            () => service.ConnectAsync("adb.exe", "serial", "General"));
        await Task.Delay(50);
        Assert.DoesNotContain(nameof(FakeUmaNativeApi.Connect), native.Calls);

        continueLoad.Set();
        await reload;
        await Assert.ThrowsAsync<ManagedBridgeException>(() => connect);
        Assert.Equal(
            [nameof(FakeUmaNativeApi.LoadResource), nameof(FakeUmaNativeApi.RefreshResourceSnapshot),
                nameof(FakeUmaNativeApi.Connect)],
            native.Calls);
    }

    [Fact]
    public async Task ResourceReloadRejectsAnActiveTemplateReadBeforeChangingTheGlobalSnapshot()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi { BeforeLoadTemplateReturn = () => { entered.Set(); release.Wait(); } };
        await using var service = CreateService(native);
        await Initialize(service);
        native.Calls.Clear();
        var read = Task.Run(() => service.LoadTemplateImage("template.png", TemplatePreparation.MaskedHead));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReloadResourceAsync(_root));
            Assert.DoesNotContain(nameof(FakeUmaNativeApi.LoadResource), native.Calls);
            Assert.DoesNotContain(nameof(FakeUmaNativeApi.RefreshResourceSnapshot), native.Calls);
        }
        finally { release.Set(); }
        await read;
        await service.ReloadResourceAsync(_root);
        Assert.Contains(nameof(FakeUmaNativeApi.RefreshResourceSnapshot), native.Calls);
    }

    [Fact]
    public async Task InitializeCannotRunTwice()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Initialize(service));
    }

    [Fact]
    public async Task DisposeRacingWithCreateDestroysTheUnassignedHandle()
    {
        using var createReachedReturn = new ManualResetEventSlim();
        using var releaseCreate = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            BeforeCreateReturn = () =>
            {
                createReachedReturn.Set();
                releaseCreate.Wait();
            },
        };
        var service = CreateService(native);
        Task initialization = Task.Run(() => Initialize(service));
        Assert.True(createReachedReturn.Wait(TimeSpan.FromSeconds(5)));

        await service.DisposeAsync();
        releaseCreate.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await initialization);

        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task ConnectBeforeInitializationIsRejected()
    {
        var native = new FakeUmaNativeApi();
        await using var service = CreateService(native);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task ConnectReplaysCallbacksDeliveredBeforeNativeReturn()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);
        var events = new List<ConnectionEvent>();
        service.ConnectionEventReceived += events.Add;

        ConnectionTerminalEvent result = await service.ConnectAsync("adb.exe", "serial", "General");

        Assert.IsType<ConnectionSucceededEvent>(result);
        Assert.Collection(
            events,
            item => Assert.IsType<ConnectionStartedEvent>(item),
            item => Assert.IsType<ConnectionProgressEvent>(item),
            item => Assert.IsType<ConnectionSucceededEvent>(item));
        Assert.Equal(new DeviceSessionIdentity(
            Path.GetFullPath("adb.exe"), "serial", 42), service.ActiveDeviceIdentity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptedReconnectDetachesOldFrameBeforeCallbacksAreReplayed(bool succeeds)
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);
        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));

        native.CaptureResult = new UmaStartResult(71, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(101, (ulong)native.FramePng.Length)));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame oldFrame = Assert.IsType<DeviceFrame>(captured.FrameLease);

        const ulong newOperationId = 84;
        native.ConnectResult = new UmaStartResult(newOperationId, 0);
        native.BeforeConnectReturn = () =>
        {
            native.Emit(1, Started(newOperationId));
            native.Emit(2, Progress(newOperationId, includePhase: true));
            native.Emit(succeeds ? 3 : 4, succeeds
                ? Succeeded(newOperationId)
                : Failed(newOperationId, (int)ConnectionErrorCode.CommandFailed));
        };

        ConnectionTerminalEvent result = await service.ConnectAsync("adb.exe", "serial", "General");

        Assert.Equal(succeeds, result is ConnectionSucceededEvent);
        Assert.True(oldFrame.IsDisposed);
        Assert.Equal(succeeds, service.ActiveDeviceIdentity is not null);
        oldFrame.Dispose();
        Assert.DoesNotContain(101UL, native.ReleasedFrameIds);
    }

    [Fact]
    public async Task RejectedReconnectPreservesTheCurrentSessionFrameLease()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);
        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));

        native.CaptureResult = new UmaStartResult(71, 0);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(101, (ulong)native.FramePng.Length)));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame currentFrame = Assert.IsType<DeviceFrame>(captured.FrameLease);

        native.BeforeConnectReturn = null;
        native.ConnectResult = new UmaStartResult(0, (int)ConnectionErrorCode.InvalidArgument);
        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));

        Assert.False(currentFrame.IsDisposed);
        currentFrame.Dispose();
        Assert.Equal([101UL], native.ReleasedFrameIds);
    }

    [Fact]
    public async Task ConnectRoutesCallbacksDeliveredAfterBinding()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);

        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");
        EmitSuccess(native, 42);

        Assert.IsType<ConnectionSucceededEvent>(await operation);
    }

    [Fact]
    public async Task DisposeClearsTheActiveDeviceIdentitySnapshot()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        var service = CreateService(native);
        await Initialize(service);
        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));
        Assert.NotNull(service.ActiveDeviceIdentity);

        await service.DisposeAsync();

        Assert.Null(service.ActiveDeviceIdentity);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(42, 11)]
    [InlineData(0, 11)]
    public async Task ConnectRejectsInvalidOrFailedStartResults(ulong operationId, int errorCode)
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(operationId, errorCode) };
        await using var service = CreateService(native);
        await Initialize(service);

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task ConnectDiagnosesIllegalCallbackFromRejectedStart()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(0, 11) };
        native.BeforeConnectReturn = () => native.Emit(1, Started(99));
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));

        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.NativeContractViolation);
    }

    [Fact]
    public async Task WrongOperationIdDoesNotCompleteCurrentOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        EmitSuccess(native, 99);
        Assert.False(operation.IsCompleted);
        EmitSuccess(native, 42);

        Assert.IsType<ConnectionSucceededEvent>(await operation);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.UnknownEvent);
    }

    [Fact]
    public async Task MalformedCallbackFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.Emit(2, Progress(42));

        await Assert.ThrowsAsync<ManagedBridgeException>(async () => await operation);
    }

    [Fact]
    public async Task NullCallbackPointerFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.EmitNull(2);

        await Assert.ThrowsAsync<ManagedBridgeException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OversizedCallbackFaultsActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        native.Emit(2, new string('a', CallbackParser.MaxCallbackJsonBytes + 1));

        await Assert.ThrowsAsync<ManagedBridgeException>(async () =>
            await operation.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SecondConnectIsRejectedWhileFirstIsActive()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> first = service.ConnectAsync("adb.exe", "serial", "General");

        await Assert.ThrowsAsync<ManagedBridgeException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
        EmitSuccess(native, 42);
        await first;

        Assert.Equal(1, native.Calls.Count(call => call == "Connect"));
    }

    [Fact]
    public async Task DuplicateAndLateTerminalCallbacksDoNotReplaceResult()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        EmitSuccess(native, 42);
        ConnectionTerminalEvent first = await operation;
        native.Emit(4, Failed(42, 7));

        Assert.IsType<ConnectionSucceededEvent>(first);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.LateEvent);
    }

    [Fact]
    public async Task TerminalEventHandlerCanStartAnotherOperationAfterCallbackCleanup()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent>? reentrant = null;
        bool startedReentrantly = false;
        service.ConnectionEventReceived += connectionEvent =>
        {
            if (connectionEvent is ConnectionTerminalEvent && !startedReentrantly)
            {
                startedReentrantly = true;
                reentrant = service.ConnectAsync("adb.exe", "serial", "General");
            }
        };
        Task<ConnectionTerminalEvent> first = service.ConnectAsync("adb.exe", "serial", "General");

        Assert.IsType<ConnectionSucceededEvent>(await first);

        Assert.NotNull(reentrant);
        Assert.IsType<ConnectionSucceededEvent>(await reentrant!);
        Assert.Equal(2, native.Calls.Count(call => call == "Connect"));
    }

    [Fact]
    public async Task TokenCancellationDuringNativeStartIsSentAfterBinding()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        native.BeforeConnectReturn = () =>
        {
            entered.Set();
            release.Wait();
        };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        using var cancellation = new CancellationTokenSource();

        Task<Task<ConnectionTerminalEvent>> start = Task.Factory.StartNew(
            () => service.ConnectAsync("adb.exe", "serial", "General", cancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        release.Set();
        Task<ConnectionTerminalEvent> operation = await start;

        var result = Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal(ConnectionErrorCode.Canceled, result.ErrorCode);
        Assert.Equal([42UL], native.CanceledOperationIds);
    }

    [Fact]
    public async Task ManualCancellationUsesBoundOperationIdOnce()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        await service.CancelOperationAsync(42);

        Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal([42UL], native.CanceledOperationIds);
    }

    [Fact]
    public async Task CancellationRegistrationIsDisposedAfterTerminalEvent()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        await using var service = CreateService(native);
        await Initialize(service);
        using var cancellation = new CancellationTokenSource();
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync(
            "adb.exe", "serial", "General", cancellation.Token);
        EmitSuccess(native, 42);
        await operation;

        cancellation.Cancel();

        Assert.Empty(native.CanceledOperationIds);
    }

    [Fact]
    public async Task DisposeDestroysIdleHandleExactlyOnce()
    {
        var native = new FakeUmaNativeApi();
        var service = CreateService(native);
        await Initialize(service);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task DisposeCancelsActiveOperationBeforeDestroy()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.CancelOperationAction = id =>
        {
            native.Emit(1, Started(id));
            native.Emit(4, Failed(id, 9));
        };
        var service = CreateService(native);
        await Initialize(service);
        Task<ConnectionTerminalEvent> operation = service.ConnectAsync("adb.exe", "serial", "General");

        await service.DisposeAsync();

        Assert.IsType<ConnectionFailedEvent>(await operation);
        Assert.Equal([42UL], native.CanceledOperationIds);
        Assert.Equal(1, native.DestroyCalls);
    }

    [Fact]
    public async Task DisposeAbandonsHandleWhenTerminalNeverArrives()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        var service = CreateService(native, TimeSpan.Zero);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        _ = service.ConnectAsync("adb.exe", "serial", "General");

        await service.DisposeAsync();

        Assert.Equal(0, native.DestroyCalls);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.FatalShutdownTimeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task DisposeReturnsWhenDestroyRemainsBlocked()
    {
        using var destroyEntered = new ManualResetEventSlim();
        using var continueDestroy = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            DestroyEntered = destroyEntered,
            ContinueDestroy = continueDestroy,
        };
        var service = CreateService(native, TimeSpan.Zero);
        await Initialize(service);

        await service.DisposeAsync();
        Assert.True(destroyEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, native.DestroyCalls);
        continueDestroy.Set();
    }

    [Fact]
    public async Task ZeroShutdownBudgetRetainsHandleWhenActiveOperationCannotDrain()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        _ = service.ConnectAsync("adb.exe", "serial", "General");

        await service.DisposeAsync(TimeSpan.Zero);

        Assert.Equal(0, native.DestroyCalls);
        Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.FatalShutdownTimeout);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.ConnectAsync("adb.exe", "serial", "General"));
    }

    [Fact]
    public async Task ReducedShutdownBudgetKeepsBlockedDestroyCallbackAlive()
    {
        using var destroyEntered = new ManualResetEventSlim();
        using var continueDestroy = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            DestroyEntered = destroyEntered,
            ContinueDestroy = continueDestroy,
        };
        var service = CreateService(native);
        await Initialize(service);
        var diagnostics = new List<BridgeDiagnostic>();
        service.DiagnosticReceived += diagnostics.Add;
        var callback = (UmaApiCallback)typeof(UmaService)
            .GetField("_nativeCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

        try
        {
            await service.DisposeAsync(TimeSpan.FromMilliseconds(50));
            Assert.True(destroyEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.FatalShutdownTimeout);
            var retained = (ConcurrentDictionary<Task, UmaApiCallback>)typeof(AbandonedNativeHandleRegistry)
                .GetField("Destroying", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            Assert.Contains(retained.Values, value => ReferenceEquals(value, callback));
        }
        finally
        {
            continueDestroy.Set();
        }
    }

    [Fact]
    public async Task DeviceOperationsUseTypedEventsWithoutConnectionStarted()
    {
        var native = new FakeUmaNativeApi { VerifyGameResult = new UmaStartResult(61, 0) };
        native.BeforeVerifyGameReturn = () => native.Emit(5,
            DeviceEnvelope(61, 4, "GameVerified", new { package_id = "com.example.game" }));
        await using var service = CreateService(native);
        await Initialize(service);

        DeviceOperationEvent result = await service.VerifyGameAsync("com.example.game");

        var verified = Assert.IsType<GameVerifiedEvent>(result);
        Assert.Equal(61UL, verified.OperationId);
        Assert.Equal(4UL, verified.Sequence);
        Assert.Equal("com.example.game", verified.PackageId);
        Assert.DoesNotContain("CancelOperation", native.Calls);
    }

    [Fact]
    public async Task Api9PipelineRoutesRuntimeDiagnosticBeforeItsTerminalEvent()
    {
        const ulong operationId = 180;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 9,
            RunPipelineResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeRunPipelineReturn = () =>
        {
            native.Emit(20, DeviceEnvelope(operationId, 1, "RuntimeDiagnostic", new
            {
                task_id = "",
                level = "ERROR",
                message = "unknown action requested",
            }));
            native.Emit(15, DeviceEnvelope(operationId, 2, "PipelineCompleted", new
            {
                status = 0,
                final_task = "done",
                total_steps = 1,
            }));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        var events = new List<ConnectionEvent>();
        service.ConnectionEventReceived += events.Add;

        PipelineRunResult result = await service.RunPipelineAsync("{}");

        Assert.True(result.Succeeded);
        Assert.Collection(events,
            item => Assert.Equal(new RuntimeDiagnosticEvent(
                operationId, 1, "", RuntimeDiagnosticLevel.Error, "unknown action requested"), item),
            item => Assert.IsType<PipelineCompletedEvent>(item));
    }

    [Fact]
    public async Task GameTaskRunUsesBufferedTypedProgressOnlyForItsOperation()
    {
        const ulong operationId = 181;
        const string settingsJson = "{\"trainee\":\"训练\"}";
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 10,
            RunGameTaskResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeRunGameTaskReturn = () =>
        {
            native.Emit(17, DeviceEnvelope(operationId, 3, "GameTaskProgress", new
            {
                task_id = "start-game",
                event_type = (int)GameTaskEventType.ActionExecuting,
                task_name = "launch_game",
                pipeline_name = "",
                step_index = 1,
                succeeded = true,
                score = 0.75,
                target_x = 0,
                target_y = 0,
                message = "starting",
                timestamp_ms = 100L,
                completed_units = 1,
                total_units = 2,
            }));
            native.Emit(18, DeviceEnvelope(operationId, 4, "GameTaskCompleted", new
            {
                task_id = "start-game",
                completed_units = 2,
                total_units = 2,
                process_detected = true,
                error_code = 0,
                message = "completed",
            }));
        };
        await using var service = CreateService(native);
        await Initialize(service);
        var progress = new List<GameTaskProgressEvent>();
        var globalEvents = new List<ConnectionEvent>();
        service.ConnectionEventReceived += globalEvents.Add;

        GameTaskRunResult result = await service.RunGameTaskAsync(
            "start-game", settingsJson, progress.Add);

        Assert.Equal(new GameTaskRunResult(
            "start-game", true, 2, 2, true, ConnectionErrorCode.Success, "completed"), result);
        Assert.Equal("start-game", native.LastRunGameTaskId);
        Assert.Equal(settingsJson, native.LastRunGameTaskSettingsJson);
        GameTaskProgressEvent progressEvent = Assert.Single(progress);
        Assert.Equal(operationId, progressEvent.OperationId);
        Assert.Equal(3UL, progressEvent.Sequence);
        Assert.Equal("start-game", progressEvent.TaskId);
        Assert.Equal(GameTaskEventType.ActionExecuting, progressEvent.EventType);
        Assert.Equal(1, progressEvent.CompletedUnits);
        Assert.Equal(2, progressEvent.TotalUnits);
        Assert.DoesNotContain(globalEvents, static item => item is GameTaskProgressEvent);
        Assert.Contains(globalEvents, static item => item is GameTaskCompletedEvent);
    }

    [Fact]
    public async Task GameTaskStartRejectionReturnsTypedErrorWithoutCallbacks()
    {
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 10,
            RunGameTaskResult = new UmaStartResult(0, (int)ConnectionErrorCode.DeviceNotReady),
        };
        await using var service = CreateService(native);
        await Initialize(service);
        var globalEvents = new List<ConnectionEvent>();
        var progress = new List<GameTaskProgressEvent>();
        service.ConnectionEventReceived += globalEvents.Add;

        GameTaskRunResult result = await service.RunGameTaskAsync(
            "mail-collection", "{}", progress.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(ConnectionErrorCode.DeviceNotReady, result.ErrorCode);
        Assert.Empty(progress);
        Assert.Empty(globalEvents);
    }

    [Fact]
    public async Task GameTaskFailurePreservesCancellationCodeForQueueControl()
    {
        const ulong operationId = 182;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 10,
            RunGameTaskResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeRunGameTaskReturn = () => native.Emit(19,
            DeviceEnvelope(operationId, 8, "GameTaskFailed", new
            {
                task_id = "mail-collection",
                completed_units = 1,
                total_units = 3,
                process_detected = false,
                error_code = (int)ConnectionErrorCode.Canceled,
                message = "cancelled",
            }));
        await using var service = CreateService(native);
        await Initialize(service);

        GameTaskRunResult result = await service.RunGameTaskAsync("mail-collection", "{}");

        Assert.False(result.Succeeded);
        Assert.Equal(ConnectionErrorCode.Canceled, result.ErrorCode);
        Assert.Equal(1, result.CompletedUnits);
        Assert.Equal(3, result.TotalUnits);
    }

    [Fact]
    public async Task QueuedGameTaskProgressPrecedesTerminalAndCompletesBeforeDispose()
    {
        const ulong operationId = 183;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 10,
            RunGameTaskResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeRunGameTaskReturn = () =>
        {
            native.Emit(17, DeviceEnvelope(operationId, 10, "GameTaskProgress", new
            {
                task_id = "mail-collection",
                event_type = (int)GameTaskEventType.ActionExecuted,
                task_name = "home",
                pipeline_name = "mail",
                step_index = 0,
                completed_units = 1,
                total_units = 1,
                succeeded = true,
                score = 1.0,
                target_x = 100,
                target_y = 200,
                message = "done",
                timestamp_ms = 100,
            }));
            native.Emit(20, DeviceEnvelope(operationId, 11, "RuntimeDiagnostic", new
            {
                task_id = "mail-collection",
                level = "INFO",
                message = "matched template",
            }));
            native.Emit(18, DeviceEnvelope(operationId, 12, "GameTaskCompleted", new
            {
                task_id = "mail-collection",
                completed_units = 1,
                total_units = 1,
                process_detected = false,
                error_code = 0,
                message = "completed",
            }));
        };

        var dispatcher = new QueuedEventDispatcher();
        var service = new UmaService(native, dispatcher);
        await Initialize(service);
        var callbackOrder = new List<string>();
        service.ConnectionEventReceived += item =>
        {
            if (item is RuntimeDiagnosticEvent)
            {
                callbackOrder.Add("diagnostic");
            }
            if (item is GameTaskCompletedEvent)
            {
                callbackOrder.Add("terminal");
            }
        };

        Task<GameTaskRunResult> run = service.RunGameTaskAsync(
            "mail-collection", "{}", _ => callbackOrder.Add("progress"));
        Assert.False(run.IsCompleted);
        Assert.Empty(callbackOrder);

        Task disposing = service.DisposeAsync(TimeSpan.FromSeconds(2)).AsTask();
        dispatcher.Drain();
        GameTaskRunResult result = await run;
        await disposing;
        Assert.True(result.Succeeded);
        Assert.Equal(["progress", "diagnostic", "terminal"], callbackOrder);

        dispatcher.Drain();
        Assert.Equal(["progress", "diagnostic", "terminal"], callbackOrder);
    }

    [Fact]
    public async Task QueuedGameTaskProgressIsSuppressedAfterDisposeTimesOut()
    {
        const ulong operationId = 184;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 10,
            RunGameTaskResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeRunGameTaskReturn = () => native.Emit(17,
            DeviceEnvelope(operationId, 1, "GameTaskProgress", new
            {
                task_id = "mail-collection",
                event_type = (int)GameTaskEventType.ActionExecuted,
                task_name = "home",
                pipeline_name = "mail",
                step_index = 0,
                completed_units = 1,
                total_units = 2,
                succeeded = true,
                score = 1.0,
                target_x = 100,
                target_y = 200,
                message = "queued before native start returned",
                timestamp_ms = 100,
            }));

        var dispatcher = new QueuedEventDispatcher();
        var service = new UmaService(native, dispatcher);
        await Initialize(service);
        var delivered = new List<ConnectionEvent>();
        service.ConnectionEventReceived += delivered.Add;
        var progress = new List<GameTaskProgressEvent>();
        Task<GameTaskRunResult> run = service.RunGameTaskAsync(
            "mail-collection", "{}", progress.Add);

        Assert.False(run.IsCompleted);
        Assert.Equal(1, dispatcher.PendingCount);

        await service.DisposeAsync(TimeSpan.Zero);

        ManagedBridgeException failure = await Assert.ThrowsAsync<ManagedBridgeException>(() => run);
        Assert.Equal(DiagnosticCategory.FatalShutdownTimeout, failure.Category);
        Assert.Equal([operationId], native.CanceledOperationIds);
        Assert.Equal(0, native.DestroyCalls);
        Assert.Empty(progress);
        Assert.Empty(delivered);
        Assert.Null(typeof(UmaService).GetField("_activeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));
        Assert.Null(typeof(UmaService).GetField("_startingOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));

        var callback = (UmaApiCallback)typeof(UmaService)
            .GetField("_nativeCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var abandoned = (ConcurrentDictionary<IntPtr, UmaApiCallback>)typeof(AbandonedNativeHandleRegistry)
            .GetField("Abandoned", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        Assert.Contains(abandoned.Values, retained => ReferenceEquals(retained, callback));

        // Drain the pump that was queued before Dispose timed out. Its captured
        // operation must no longer deliver progress after shutdown.
        dispatcher.Drain();
        Assert.Empty(progress);
        Assert.Empty(delivered);

        // A callback arriving through the retained native callback after
        // shutdown is diagnosed as late and cannot revive the completed run.
        native.Emit(18, DeviceEnvelope(operationId, 2, "GameTaskCompleted", new
        {
            task_id = "mail-collection",
            completed_units = 1,
            total_units = 2,
            process_detected = false,
            error_code = (int)ConnectionErrorCode.Success,
            message = "late terminal",
        }));
        dispatcher.Drain();

        Assert.Empty(progress);
        Assert.Empty(delivered);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task CapturedFrameKeepsNativeLeaseUntilDisposedAndSupportsInput()
    {
        var native = new FakeUmaNativeApi { CaptureResult = new UmaStartResult(71, 0) };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 5, "FrameCaptured", FrameMetadata(91, (ulong)native.FramePng.Length)));
        var service = CreateService(native);
        await Initialize(service);

        var capture = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(capture.FrameLease);
        Assert.Equal(native.FramePng, capture.PngBytes);
        Assert.Equal(native.FramePng, frame.PngBytes.ToArray());
        Assert.Empty(native.ReleasedFrameIds);

        native.TapResult = new UmaStartResult(72, 0);
        native.BeforeTapReturn = () => native.Emit(8,
            DeviceEnvelope(72, 6, "InputSucceeded", InputPayload(91, (ulong)native.FramePng.Length)));
        var tap = Assert.IsType<InputSucceededEvent>(await service.TapAsync(frame, 240, 420));
        Assert.Equal(91UL, native.LastTapFrameId);
        Assert.Equal(91UL, tap.SourceFrame.FrameId);
        Assert.Empty(native.ReleasedFrameIds);

        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Equal([91UL], native.ReleasedFrameIds);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TapAsync(frame, 1, 1));
        await service.DisposeAsync();
        Assert.Equal([91UL], native.ReleasedFrameIds);
    }

    [Fact]
    public async Task WrongOperationFrameCallbackCannotReleaseAnAlreadyLeasedFrame()
    {
        var native = new FakeUmaNativeApi { CaptureResult = new UmaStartResult(91, 0) };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(91, 1, "FrameCaptured", FrameMetadata(101, (ulong)native.FramePng.Length)));
        var service = CreateService(native);
        await Initialize(service);
        var first = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());

        native.CaptureResult = new UmaStartResult(92, 0);
        native.BeforeCaptureReturn = () =>
        {
            native.Emit(7, DeviceEnvelope(999, 2, "FrameCaptured", FrameMetadata(101, (ulong)native.FramePng.Length)));
            native.Emit(7, DeviceEnvelope(92, 3, "FrameCaptured", FrameMetadata(102, (ulong)native.FramePng.Length)));
        };
        var second = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());

        Assert.Empty(native.ReleasedFrameIds);
        Assert.Equal(101UL, first.FrameLease?.FrameId);
        Assert.Equal(102UL, second.FrameLease?.FrameId);
        first.FrameLease!.Dispose();
        second.FrameLease!.Dispose();
        Assert.Equal([101UL, 102UL], native.ReleasedFrameIds);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task FrameCopyFailureFaultsOperationAndReleasesUnretainedNativeFrame()
    {
        var native = new FakeUmaNativeApi
        {
            CaptureResult = new UmaStartResult(111, 0),
            CopyFramePngResult = (int)ConnectionErrorCode.CommandFailed,
        };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(111, 1, "FrameCaptured", FrameMetadata(1111, (ulong)native.FramePng.Length)));
        await using var service = CreateService(native);
        await Initialize(service);

        var error = await Assert.ThrowsAsync<ManagedBridgeException>(() => service.CaptureAsync());

        Assert.Equal(DiagnosticCategory.NativeContractViolation, error.Category);
        Assert.Equal([1111UL], native.ReleasedFrameIds);
        Assert.IsType<ConnectionSucceededEvent>(await StartSuccessfulConnectionAfterDeviceFailure(service, native));
    }

    [Fact]
    public async Task DisposeStillDestroysHandleWhenFrameCopyFaultsWhileItWaits()
    {
        using var copyEntered = new ManualResetEventSlim();
        using var continueCopy = new ManualResetEventSlim();
        var native = new FakeUmaNativeApi
        {
            CaptureResult = new UmaStartResult(121, 0),
            ThrowOnCopyFramePng = true,
            CopyFrameEntered = copyEntered,
            ContinueCopyFrame = continueCopy,
        };
        var service = CreateService(native);
        await Initialize(service);
        Task<DeviceOperationEvent> operation = service.CaptureAsync();
        Task callback = Task.Run(() => native.Emit(7,
            DeviceEnvelope(121, 1, "FrameCaptured", FrameMetadata(1211, (ulong)native.FramePng.Length))));
        Assert.True(copyEntered.Wait(TimeSpan.FromSeconds(5)));

        ValueTask dispose = service.DisposeAsync();
        continueCopy.Set();
        await callback;
        await Assert.ThrowsAsync<ManagedBridgeException>(() => operation);
        await dispose;

        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal([1211UL], native.ReleasedFrameIds);
    }

    [Fact]
    public async Task ServiceShutdownReleasesOutstandingFrameLeases()
    {
        var native = new FakeUmaNativeApi { CaptureResult = new UmaStartResult(81, 0) };
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(81, 7, "FrameCaptured", FrameMetadata(101, (ulong)native.FramePng.Length)));
        var service = CreateService(native);
        await Initialize(service);

        var capture = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(capture.FrameLease);
        await service.DisposeAsync();

        Assert.True(frame.IsDisposed);
        Assert.Equal([101UL], native.ReleasedFrameIds);
        frame.Dispose();
        Assert.Equal([101UL], native.ReleasedFrameIds);
    }

    [Fact]
    public async Task DisconnectUsesSessionGenerationAndCancelsTheActiveOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        native.CancelOperationAction = operationId => native.Emit(11,
            DeviceEnvelope(operationId, 9, "OperationFailed", new
            {
                error_code = 9,
                phase = "cancel",
                message = "cancelled",
            }));
        await using var service = CreateService(native);
        await Initialize(service);
        var events = new List<ConnectionEvent>();
        service.ConnectionEventReceived += events.Add;

        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));
        native.VerifyGameResult = new UmaStartResult(43, 0);
        Task<DeviceOperationEvent> operation = service.VerifyGameAsync("com.example.game");
        native.Emit(10, SessionEnvelope(8, "DeviceDisconnected", new
        {
            error_code = 15,
            session_generation = 42,
        }));
        DeviceOperationEvent terminal = await operation;

        Assert.IsType<DeviceOperationFailedEvent>(terminal);
        var disconnected = Assert.IsType<DeviceDisconnectedEvent>(Assert.Single(events.OfType<DeviceDisconnectedEvent>()));
        Assert.Equal(42UL, disconnected.SessionGeneration);
        Assert.Equal(8UL, disconnected.Sequence);
        Assert.Equal([43UL], native.CanceledOperationIds);
        Assert.Null(service.ActiveDeviceIdentity);
    }

    [Fact]
    public async Task OldSessionDisconnectDuringNewConnectDoesNotCancelTheNewConnect()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);

        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));

        native.BeforeConnectReturn = null;
        native.ConnectResult = new UmaStartResult(84, 0);
        Task<ConnectionTerminalEvent> reconnect = service.ConnectAsync("adb.exe", "serial", "General");
        native.Emit(10, SessionEnvelope(9, "DeviceDisconnected", new
        {
            error_code = 15,
            session_generation = 42,
        }));
        EmitSuccess(native, 84);

        Assert.IsType<ConnectionSucceededEvent>(await reconnect);
        Assert.Empty(native.CanceledOperationIds);
    }

    [Fact]
    public async Task OldSessionDisconnectAfterReconnectDoesNotCancelNewSessionOperation()
    {
        var native = new FakeUmaNativeApi { ConnectResult = new UmaStartResult(42, 0) };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        await using var service = CreateService(native);
        await Initialize(service);

        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));

        native.BeforeConnectReturn = null;
        native.ConnectResult = new UmaStartResult(84, 0);
        Task<ConnectionTerminalEvent> reconnect = service.ConnectAsync("adb.exe", "serial", "General");
        EmitSuccess(native, 84);
        Assert.IsType<ConnectionSucceededEvent>(await reconnect);

        native.VerifyGameResult = new UmaStartResult(99, 0);
        Task<DeviceOperationEvent> verify = service.VerifyGameAsync("com.example.game");
        native.Emit(10, SessionEnvelope(10, "DeviceDisconnected", new
        {
            error_code = 15,
            session_generation = 42,
        }));
        native.Emit(5, DeviceEnvelope(99, 1, "GameVerified", new { package_id = "com.example.game" }));

        var verified = Assert.IsType<GameVerifiedEvent>(await verify);
        Assert.Equal("com.example.game", verified.PackageId);
        Assert.Empty(native.CanceledOperationIds);
        Assert.Equal(new DeviceSessionIdentity(
            Path.GetFullPath("adb.exe"), "serial", 84), service.ActiveDeviceIdentity);
    }

    [Fact]
    public async Task NativeIdleReconnectDetachesOldFramesBeforeSynchronousListenersStartWork()
    {
        var native = new FakeUmaNativeApi
        {
            ConnectResult = new UmaStartResult(42, 0),
            CaptureResult = new UmaStartResult(71, 0),
            VerifyGameResult = new UmaStartResult(43, 0),
        };
        native.BeforeConnectReturn = () => EmitSuccess(native, 42);
        native.BeforeCaptureReturn = () => native.Emit(7,
            DeviceEnvelope(71, 1, "FrameCaptured", FrameMetadata(91, (ulong)native.FramePng.Length)));
        await using var service = CreateService(native);
        await Initialize(service);
        Assert.IsType<ConnectionSucceededEvent>(
            await service.ConnectAsync("adb.exe", "serial", "General"));
        var captured = Assert.IsType<FrameCapturedEvent>(await service.CaptureAsync());
        DeviceFrame frame = Assert.IsType<DeviceFrame>(captured.FrameLease);

        Task<DeviceOperationEvent>? verification = null;
        bool listenerSawDetachedFrame = false;
        service.ConnectionEventReceived += connectionEvent =>
        {
            if (connectionEvent is DeviceReconnectedEvent)
            {
                listenerSawDetachedFrame = frame.IsDisposed;
                verification = service.VerifyGameAsync("com.example.game");
            }
        };

        native.Emit(12, SessionEnvelope(2, "DeviceReconnected", new
        {
            previous_session_generation = 42,
            session_generation = 84,
            serial = "reconnected-serial",
            width = 1080,
            height = 1920,
            physical_width = 1080,
            physical_height = 1920,
            size_source = "physical",
        }));

        Assert.True(listenerSawDetachedFrame);
        Assert.Equal(new DeviceSessionIdentity(
            Path.GetFullPath("adb.exe"), "reconnected-serial", 84), service.ActiveDeviceIdentity);
        Assert.Empty(native.ReleasedFrameIds);
        Assert.NotNull(verification);
        native.Emit(10, SessionEnvelope(3, "DeviceDisconnected", new
        {
            error_code = 15,
            session_generation = 42,
        }));
        Assert.Empty(native.CanceledOperationIds);
        native.Emit(5, DeviceEnvelope(43, 1, "GameVerified", new { package_id = "com.example.game" }));
        Assert.IsType<GameVerifiedEvent>(await verification!);

        frame.Dispose();
        Assert.Empty(native.ReleasedFrameIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task Initialize(UmaService service) =>
        service.InitializeAsync(_root, Path.Combine(_root, "app-data"));

    private static async Task<ConnectionTerminalEvent> StartSuccessfulConnectionAfterDeviceFailure(
        UmaService service,
        FakeUmaNativeApi native)
    {
        native.ConnectResult = new UmaStartResult(131, 0);
        native.BeforeConnectReturn = () => EmitSuccess(native, 131);
        return await service.ConnectAsync("adb.exe", "serial", "General");
    }

    private static UmaService CreateService(FakeUmaNativeApi native, TimeSpan? shutdownTimeout = null) =>
        new(native, new InlineEventDispatcher(), shutdownTimeout ?? TimeSpan.FromSeconds(10), TimeProvider.System);

    private static void EmitSuccess(FakeUmaNativeApi native, ulong operationId)
    {
        native.Emit(1, Started(operationId));
        native.Emit(2, Progress(operationId, includePhase: true));
        native.Emit(3, Succeeded(operationId));
    }

    private static string Started(ulong operationId) =>
        Envelope(operationId, "ConnectionStarted", new { });

    private static string Progress(ulong operationId, bool includePhase = false) => includePhase
        ? Envelope(operationId, "ConnectionProgress", new { phase = "adb_devices" })
        : Envelope(operationId, "ConnectionProgress", new { });

    private static string Succeeded(ulong operationId) =>
        Envelope(operationId, "ConnectionSucceeded", new
        {
            serial = "serial",
            android_id = "0123456789abcdef",
            android_version = "14",
            width = 1080,
            height = 1920,
            physical_width = 1080,
            physical_height = 1920,
            size_source = "physical",
        });

    private static string Failed(ulong operationId, int errorCode) =>
        Envelope(operationId, "ConnectionFailed", new
        {
            error_code = errorCode,
            phase = "cancel",
            message = "failed",
            attempt = 1,
            max_attempts = 1,
        });

    private static string Envelope(ulong operationId, string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            operation_id = operationId,
            type,
            payload,
        });

    private static string DeviceEnvelope(ulong operationId, ulong sequence, string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            version = 2,
            operation_id = operationId,
            sequence,
            type,
            payload,
        });

    private static string SessionEnvelope(ulong sequence, string type, object payload) =>
        JsonSerializer.Serialize(new
        {
            version = 2,
            operation_id = 0,
            sequence,
            type,
            payload,
        });

    private static object FrameMetadata(ulong frameId, ulong byteCount) => new
    {
        frame_id = frameId,
        session_generation = 3,
        geometry_generation = 4,
        width = 1080,
        height = 1920,
        orientation = 0,
        byte_count = byteCount,
        transform = new
        {
            canonical_width = 1080,
            canonical_height = 1920,
            rotation_degrees = 0,
            device_width = 1080,
            device_height = 1920,
            scale = 1.0,
            offset_x = 0.0,
            offset_y = 0.0,
            content_width = 1080.0,
            content_height = 1920.0,
        },
    };

    private static object InputPayload(ulong frameId, ulong byteCount) => new
    {
        source_frame = FrameMetadata(frameId, byteCount),
        requested = new { first_point = new { canonical_x = 240, canonical_y = 420 } },
        actual = new { first_point = new { device_x = 240, device_y = 420 } },
    };

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class QueuedEventDispatcher : IEventDispatcher
    {
        private readonly Queue<Action> _actions = new();

        internal int PendingCount
        {
            get
            {
                lock (_actions)
                {
                    return _actions.Count;
                }
            }
        }

        public void Post(Action action)
        {
            lock (_actions)
            {
                _actions.Enqueue(action);
            }
        }

        internal void Drain()
        {
            while (true)
            {
                Action? action;
                lock (_actions)
                {
                    action = _actions.TryDequeue(out Action? next) ? next : null;
                }

                if (action is null)
                {
                    return;
                }

                action();
            }
        }
    }
}
