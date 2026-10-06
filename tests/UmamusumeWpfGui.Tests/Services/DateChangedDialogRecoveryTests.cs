using System.IO;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class DateChangedDialogRecoveryTests
{
    [Theory]
    [InlineData(900, 1600)]
    [InlineData(720, 1280)]
    [InlineData(1080, 1920)]
    public void Captured_modal_matches_at_scaled_resolutions(int width, int height)
    {
        var fixture = new Fixture();
        var frame = Load("common/date_changed/sample.png");
        if (width != 900)
        {
            using var resized = Image.LoadPixelData<Rgba32>(frame.RgbaPixels!, 900, 1600);
            resized.Mutate(operation => operation.Resize(width, height));
            var bytes = new byte[width * height * 4];
            resized.CopyPixelDataTo(bytes);
            frame = GrayImageCodec.FromScreenshot(new(AdbScreenshotMethod.Raw, [], TimeSpan.Zero,
                new AdbRawScreenshot(width, height, bytes)))!;
        }
        var button = fixture.Guard.Detect(frame);
        Assert.NotNull(button);
        Assert.InRange(button.CenterX, width * .49, width * .51);
        Assert.InRange(button.CenterY, height * .64, height * .67);
    }

    [Theory]
    [InlineData("rest_confirmation.png")]
    [InlineData("infirmary_confirm.png")]
    [InlineData("recreation_confirmation.png")]
    [InlineData("summer_rest_dialog.png")]
    [InlineData("career_skill_learn_sample.png")]
    [InlineData("career_skill_obtained_sample.png")]
    public void Other_green_dialogs_and_skills_do_not_match(string capture)
    {
        Assert.Null(new Fixture().Guard.Detect(Load("ura/screens/captures/" + capture)));
    }

    [Theory]
    [InlineData("title.png", 330, 492)]
    [InlineData("body.png", 347, 751)]
    [InlineData("ok.png", 271, 993)]
    public void Materials_preserve_every_original_pixel_and_background(string name, int x, int y)
    {
        var source = Load("common/date_changed/sample.png");
        var crop = Load("common/date_changed/" + name);
        for (var row = 0; row < crop.Height; row++)
        for (var column = 0; column < crop.Width; column++)
        {
            var offset = (row * crop.Width + column) * 4;
            Assert.Equal((byte)255, crop.RgbaPixels![offset + 3]);
            Assert.Equal(source.RgbaPixels!.AsSpan(((row + y) * source.Width + column + x) * 4, 4).ToArray(),
                crop.RgbaPixels.AsSpan(offset, 4).ToArray());
        }
    }

    [Fact]
    public async Task Two_stable_frames_interrupt_before_a_skills_back_fallback_tap()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        using var scope = new GameAutomationScope(null, null);
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() =>
            visual.TapAsync(Connection(), 95, 1541, 900, 1600, "career_skill.back_fallback"));
        Assert.Empty(fixture.Events);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task One_transient_frame_does_not_interrupt()
    {
        var fixture = new Fixture();
        fixture.NextFrame = Blank();
        using var scope = new GameAutomationScope(null, null);
        Assert.False(await fixture.Guard.InspectAsync(Connection(), fixture.Frame, CancellationToken.None));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task Verified_rest_frame_taps_without_another_adb_screenshot()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        using var scope = new GameAutomationScope(null, null);
        var frame = Load("ura/screens/captures/rest_confirmation.png");
        await visual.TapMatchAsync(Connection(), frame, new(true, 1, 470, 995, 360, 120), "rest.confirm");
        Assert.Equal(0, fixture.CaptureCount);
        Assert.Single(fixture.Events);
    }

    [Fact]
    public async Task Verified_frame_with_daily_reset_interrupts_before_the_tap()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        using var scope = new GameAutomationScope(null, null);
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() =>
            visual.TapMatchAsync(Connection(), fixture.Frame, new(true, 1, 470, 995, 360, 120), "rest.confirm"));
        Assert.Equal(1, fixture.CaptureCount);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task Screenshots_outside_an_automation_scope_never_dismiss_the_modal()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        var frame = await visual.CaptureGrayAsync(Connection());
        Assert.NotNull(fixture.Guard.Detect(frame!));
        Assert.Empty(fixture.Events);
        Assert.Equal(1, fixture.CaptureCount);
    }

    [Fact]
    public async Task Recovery_orders_ok_launch_startup_and_requires_verified_home()
    {
        var fixture = new Fixture();
        using var scope = new GameAutomationScope(null, null);
        await fixture.Recovery.RecoverAsync(Connection(), CancellationToken.None);
        Assert.Equal(["ok", "launch", "startup"], fixture.Events);
        Assert.False(scope.Recovering);
        Assert.Equal(1, scope.Generation);
        Assert.Equal("configured.package", fixture.Package);
    }

    [Fact]
    public async Task Startup_success_without_home_is_a_recovery_failure()
    {
        var fixture = new Fixture { HomeDetected = false };
        using var scope = new GameAutomationScope(null, null);
        var error = await Assert.ThrowsAsync<DateChangedRecoveryException>(() =>
            fixture.Recovery.RecoverAsync(Connection(), CancellationToken.None));
        Assert.Contains("confirm Home", error.Message);
        Assert.False(scope.Recovering);
        Assert.Equal(0, scope.Generation);
    }

    [Fact]
    public async Task Another_date_popup_inside_startup_is_dismissed_without_recursive_startup()
    {
        var fixture = new Fixture { PopupDuringStartup = true };
        using var scope = new GameAutomationScope(null, null);
        await fixture.Recovery.RecoverAsync(Connection(), CancellationToken.None);
        Assert.Equal(["ok", "launch", "startup", "ok"], fixture.Events);
        Assert.Equal(2, scope.Generation);
    }

    [Fact]
    public async Task Canceling_recovery_does_not_launch_or_resume()
    {
        var fixture = new Fixture();
        using var scope = new GameAutomationScope(null, null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Recovery.RecoverAsync(Connection(), canceled.Token));
        Assert.Empty(fixture.Events);
        Assert.False(scope.Recovering);
    }

    [Fact]
    public void Purchase_and_race_progress_survive_new_navigation_attempts()
    {
        var definition = new HachimiPipelineDefinition { BaseDirectory = AppContext.BaseDirectory };
        var options = new HachimiPipelineRunOptions
        {
            SemanticProfile = HachimiTaskLogProfile.TeamRace,
            MaxTimesOverrides = new Dictionary<string, int> { ["raceAdvance"] = 2 },
        };
        using var scope = new GameAutomationScope(null, null);
        HachimiJsonPipelineRunner.RecordRecoverySubmission(definition, "itemRace", options);
        Assert.Equal(1, scope.PendingRaceUnits[options.SemanticProfile]);
        HachimiJsonPipelineRunner.ObserveRecoveryEvidence(definition, "next", options);
        HachimiJsonPipelineRunner.ObserveRecoveryEvidence(definition, "raceagain", options);
        Assert.Empty(scope.PendingRaceUnits);
        Assert.Equal(1, scope.ConfirmedRaceUnits[options.SemanticProfile]);
        Assert.Equal(3, HachimiJsonPipelineRunner.RequestedRaceUnits(options));
        HachimiJsonPipelineRunner.RecordRecoverySubmission(definition, "shopExchangeConfirm", options);
        Assert.Single(scope.PendingShopExchanges);
        HachimiJsonPipelineRunner.ObserveRecoveryEvidence(definition, "shopExchangeComplete", options);
        Assert.Empty(scope.PendingShopExchanges);
        Assert.Single(scope.ConfirmedShopExchanges);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Daily_race_result_confirms_exactly_the_submitted_ticket_count(int requested)
    {
        using var scope = new GameAutomationScope(null, null);
        var definition = new HachimiPipelineDefinition();
        var options = new HachimiPipelineRunOptions
        {
            SemanticProfile = HachimiTaskLogProfile.DailyRace,
            MaxTimesOverrides = new Dictionary<string, int> { ["multiRaceModeGate"] = requested - 1 },
        };
        var submission = requested == 1 ? "itemsRace" : "multiRaceTicketConfirm";
        var result = requested == 1 ? "racePlaybackResult" : "multiRaceComplete";
        HachimiJsonPipelineRunner.RecordRecoverySubmission(definition, submission, options);
        Assert.Equal(requested, scope.PendingRaceUnits[options.SemanticProfile]);
        HachimiJsonPipelineRunner.ObserveRecoveryEvidence(definition, result, options);
        HachimiJsonPipelineRunner.ObserveRecoveryEvidence(definition, result, options);
        Assert.Empty(scope.PendingRaceUnits);
        Assert.Equal(requested, scope.ConfirmedRaceUnits[options.SemanticProfile]);
    }

    [Theory]
    [InlineData(HachimiTaskLogProfile.TeamRace)]
    [InlineData(HachimiTaskLogProfile.DailyRace)]
    [InlineData(HachimiTaskLogProfile.MailCollection)]
    [InlineData(HachimiTaskLogProfile.MissionCollection)]
    public async Task Json_runner_restarts_from_home_instead_of_following_the_old_error_branch(HachimiTaskLogProfile profile)
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        var runner = new HachimiJsonPipelineRunner(fixture.Adb, visual, fixture.Settings, fixture.Recovery);
        var definition = new HachimiPipelineDefinition
        {
            Tasks = new Dictionary<string, HachimiPipelineTask>
            {
                ["home"] = new() { Algorithm = "JustReturn", Action = "ClickRect", SpecificRect = [0, 0, 20, 20], Next = ["complete"], OnErrorNext = ["bad"] },
                ["bad"] = new() { Algorithm = "JustReturn", Action = "Stop" },
                ["complete"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Success = true },
            },
        };
        var result = await runner.RunAsync(Connection(), definition, "home", new() { SemanticProfile = profile });
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["ok", "launch", "startup", "input"], fixture.Events);
        Assert.Null(GameAutomationScope.Current);
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("hsv")]
    [InlineData("template")]
    public async Task Screenshot_consumers_interrupt_before_their_own_long_timeout(string consumer)
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        using var scope = new GameAutomationScope(null, null, Connection());
        Func<Task> operation = consumer switch
        {
            "ocr" => async () => { await visual.DetectTextAsync(Connection(), null, 900, 1600, null, "test"); },
            "hsv" => async () => { await visual.ProbeHsvAsync(Connection(), 450, 800, null, 3, 900, 1600,
                0, 360, 0, 1, 0, 1, .5, 120000, 300, "test"); },
            _ => async () => { await visual.WaitForMatchAsync(Connection(), "title.png", null, .99,
                900, 1600, 120000, 300, "test", ResourcePathRuntime.Resolve("resource/hachimi/common/date_changed")); },
        };
        await Assert.ThrowsAsync<DateChangedInterruptionException>(operation);
        Assert.Empty(fixture.Events);
        Assert.Equal(2, fixture.CaptureCount);
    }

    [Fact]
    public async Task A_nested_json_action_propagates_the_interruption_to_its_owning_career()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        var runner = new HachimiJsonPipelineRunner(fixture.Adb, visual, fixture.Settings, fixture.Recovery);
        using var scope = new GameAutomationScope(null, null, Connection());
        var definition = new HachimiPipelineDefinition
        {
            Tasks = new Dictionary<string, HachimiPipelineTask>
            {
                ["skills_back"] = new() { Algorithm = "JustReturn", Action = "ClickRect", SpecificRect = [95, 1541, 0, 0], OnErrorNext = ["bad"] },
                ["bad"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Success = true },
            },
        };
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() => runner.RunAsync(Connection(), definition, "skills_back"));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task Canceling_during_startup_does_not_confirm_task_resumption()
    {
        using var canceled = new CancellationTokenSource();
        var fixture = new Fixture { AfterStartup = canceled.Cancel };
        using var scope = new GameAutomationScope(null, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Recovery.RecoverAsync(Connection(), canceled.Token));
        Assert.Equal(0, scope.Generation);
        Assert.False(scope.Recovering);
    }

    [Fact]
    public async Task Long_wait_is_interrupted_without_waiting_for_the_whole_loading_delay()
    {
        var fixture = new Fixture();
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        using var scope = new GameAutomationScope(null, null, Connection());
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() => visual.DelayAsync(60000));
        Assert.Equal([1000d, 120d], fixture.Delays);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Real_startup_screenshot_path_dismisses_resets_and_verifies_home(int repeatAtCapture)
    {
        var fixture = new Fixture
        {
            AfterOkFrame = HomeFrame(),
            PopupAtCapture = repeatAtCapture,
        };
        var startup = new AdbStartGamePipeline(fixture.Adb, fixture.Delay, fixture.Guard);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await startup.RunAsync(Connection(), "configured.package", cancellationToken: timeout.Token);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.HomeDetected);
        Assert.Equal(repeatAtCapture == 0 ? 1 : 2, fixture.Events.Count);
        Assert.All(fixture.Events, value => Assert.Equal("ok", value));
        Assert.Null(GameAutomationScope.Current);
    }

    [Fact]
    public async Task Ok_that_stays_visible_is_bounded_to_three_taps_and_ten_seconds()
    {
        var fixture = new Fixture { KeepModalAfterTap = true, UseRealDelay = true };
        using var scope = new GameAutomationScope(null, null);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<DateChangedRecoveryException>(() =>
            fixture.Recovery.RecoverAsync(Connection(), CancellationToken.None));
        Assert.Contains("ten seconds", error.Message);
        Assert.Equal(["ok", "ok", "ok"], fixture.Events);
        Assert.InRange(started.Elapsed.TotalSeconds, 9, 13);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Race_recovery_runs_only_the_remaining_confirmed_work_or_stops_an_uncertain_submission(bool confirmed)
    {
        var fixture = new Fixture { Frame = Blank() };
        var interrupted = false;
        fixture.AfterInput = x =>
        {
            if (!interrupted && x == (confirmed ? 30 : 10))
            {
                interrupted = true;
                fixture.Frame = Load("common/date_changed/sample.png");
            }
        };
        HachimiPipelineTask Click(int x, string next, string? count = null) => new()
        {
            Algorithm = "JustReturn", Action = "ClickRect", SpecificRect = [x, 0, 0, 0], Next = [next],
            PostDelay = 1500, CountAs = count,
        };
        var definition = new HachimiPipelineDefinition
        {
            Tasks = new Dictionary<string, HachimiPipelineTask>
            {
                ["home"] = Click(20, "itemRace"),
                ["itemRace"] = Click(10, "next"),
                ["next"] = Click(30, "raceagain"),
                ["raceagain"] = Click(40, "itemRace", "race"),
                ["finalNext"] = Click(50, "complete", "race"),
                ["complete"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Success = true },
            },
        };
        definition.Tasks["raceagain"].CountKey = "raceAdvance";
        definition.Tasks["raceagain"].ExceededNext = ["finalNext"];
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        var runner = new HachimiJsonPipelineRunner(fixture.Adb, visual, fixture.Settings, fixture.Recovery);
        var result = await runner.RunAsync(Connection(), definition, "home", new()
        {
            SemanticProfile = HachimiTaskLogProfile.TeamRace,
            MaxTimesOverrides = new Dictionary<string, int> { ["raceAdvance"] = 1 },
        });
        Assert.Equal(confirmed, result.Succeeded);
        Assert.Equal(confirmed ? 2 : 0, result.CompletedUnits);
        Assert.Equal(confirmed ? 2 : 1, fixture.InputXs.Count(x => x == 10));
        Assert.DoesNotContain(40, fixture.InputXs);
        if (!confirmed)
            Assert.Contains("will not be submitted again", result.Message);
    }

    [Fact]
    public async Task A_confirmed_shop_exchange_is_skipped_after_home_recovery()
    {
        var fixture = new Fixture { Frame = Blank() };
        var interrupted = false;
        fixture.AfterInput = x =>
        {
            if (!interrupted && x == 30)
            {
                interrupted = true;
                fixture.Frame = Load("common/date_changed/sample.png");
            }
        };
        HachimiPipelineTask Click(int x, string next) => new()
        {
            Algorithm = "JustReturn", Action = "ClickRect", SpecificRect = [x, 0, 0, 0], Next = [next], PostDelay = 1500,
        };
        var definition = new HachimiPipelineDefinition
        {
            Tasks = new Dictionary<string, HachimiPipelineTask>
            {
                ["home"] = Click(20, "shopProbe"),
                ["shopProbe"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Next = ["shopExchangeConfirm"] },
                ["shopExchangeConfirm"] = Click(10, "shopExchangeComplete"),
                ["shopExchangeComplete"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Next = ["shopClose"] },
                ["shopClose"] = Click(30, "shopBack"),
                ["shopBack"] = new() { Algorithm = "JustReturn", Action = "DoNothing", Success = true },
            },
        };
        var visual = new AdbVisualPipelineRuntime(fixture.Adb, fixture.Delay, new WindowsOcrTextRecognizer(), fixture.Guard);
        var runner = new HachimiJsonPipelineRunner(fixture.Adb, visual, fixture.Settings, fixture.Recovery);
        var result = await runner.RunAsync(Connection(), definition, "home");
        Assert.True(result.Succeeded, result.Message);
        Assert.Single(fixture.InputXs, x => x == 10);
    }

    private static GrayImage HomeFrame()
    {
        var marker = GrayImageCodec.FromFile(ResourcePathRuntime.Resolve(
            "resource/hachimi/pipelines/templates/start_game/game_home_selected.png"))!;
        var pixels = new byte[900 * 1600 * 4];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        for (var row = 0; row < marker.Height; row++)
            Buffer.BlockCopy(marker.RgbaPixels!, row * marker.Width * 4, pixels,
                ((1470 + row) * 900 + 350) * 4, marker.Width * 4);
        return GrayImageCodec.FromScreenshot(new(AdbScreenshotMethod.Raw, [], TimeSpan.Zero,
            new AdbRawScreenshot(900, 1600, pixels)))!;
    }

    private static LastVerifiedConnection Connection() => new("adb", "serial", "android", "version",
        900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
    private static GrayImage Blank() => new(900, 1600, new byte[900 * 1600], new byte[900 * 1600 * 4]);
    private static GrayImage Load(string path)
    {
        var resolved = ResourcePathRuntime.Resolve("resource/hachimi/" + path);
        if (!File.Exists(resolved) && path.StartsWith("ura/screens/captures/", StringComparison.Ordinal))
            resolved = CareerTestResourceResolver.FindUraCapture(
                CareerTestResourceResolver.FindWorkspaceRoot(), Path.GetFileName(path));
        return GrayImageCodec.FromFile(resolved) ?? throw new FileNotFoundException(path);
    }

    internal sealed class Fixture : IStartGamePipeline, IGameLauncher, ISettingsService, IAsyncDelay
    {
        internal readonly List<string> Events = [];
        internal GrayImage Frame = DateChangedDialogRecoveryTests.Load("common/date_changed/sample.png");
        internal GrayImage? NextFrame;
        internal GrayImage? AfterOkFrame;
        internal Action<int>? AfterInput;
        internal Action? AfterStartup;
        internal bool KeepModalAfterTap;
        internal bool UseRealDelay;
        internal int PopupAtCapture;
        internal readonly List<int> InputXs = [];
        internal readonly List<double> Delays = [];
        internal int CaptureCount;
        internal bool HomeDetected = true;
        internal bool PopupDuringStartup;
        internal string? Package;
        internal IAdbRuntime Adb { get; }
        internal DateChangedDialogGuard Guard { get; }
        internal DateChangedDialogRecovery Recovery { get; }
        internal ISettingsService Settings => this;
        internal IAsyncDelay Delay => this;
        internal Fixture()
        {
            Adb = DispatchProxy.Create<IAdbRuntime, AdbStub>();
            ((AdbStub)(object)Adb).Fixture = this;
            Guard = new DateChangedDialogGuard(Adb, this);
            Recovery = new DateChangedDialogRecovery(Guard, this, this, this);
        }
        public ConnectionSettings Load() => new() { TargetPackageIds = ["configured.package"] };
        public void Save(ConnectionSettings settings) { }
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(duration.TotalMilliseconds);
            return UseRealDelay ? Task.Delay(duration, cancellationToken) : Task.CompletedTask;
        }
        public Task<GameLaunchResult> StartAsync(string adbPath, string serial, string packageName,
            CancellationToken cancellationToken = default) => StartAsync(adbPath, serial, packageName, null, cancellationToken);
        public Task<GameLaunchResult> StartAsync(string adbPath, string serial, string packageName,
            string? activityName, CancellationToken cancellationToken = default)
        {
            Package = packageName;
            Events.Add("launch");
            return Task.FromResult(new GameLaunchResult(true, true, "running", new("", "", 0, false, null)));
        }
        public Task<GameLaunchResult> StopAsync(string adbPath, string serial, string packageName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<StartGamePipelineResult> RunAsync(LastVerifiedConnection connection, string packageName,
            IGrassTaskLogSink? logSink = null, IHachimiTaskLogSink? taskLogSink = null, CancellationToken cancellationToken = default)
        {
            Events.Add("startup");
            if (PopupDuringStartup)
            {
                Frame = DateChangedDialogRecoveryTests.Load("common/date_changed/sample.png");
                Assert.True(await Guard.InspectAsync(connection, Frame, cancellationToken));
            }
            AfterStartup?.Invoke();
            return new(true, HomeDetected, "startup-result");
        }
    }

    public class AdbStub : DispatchProxy
    {
        private Fixture _fixture = null!;
        internal Fixture Fixture { set => _fixture = value; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "DecodeRawScreenshotAsync")
            {
                _fixture.CaptureCount++;
                if (_fixture.PopupAtCapture == _fixture.CaptureCount)
                    _fixture.Frame = Load("common/date_changed/sample.png");
                var frame = _fixture.NextFrame ?? _fixture.Frame;
                return Task.FromResult(new AdbRuntimeQueryResult<AdbRawScreenshot>(
                    new(frame.Width, frame.Height, frame.RgbaPixels!), []));
            }
            if (targetMethod?.Name == "TapAsync")
            {
                var isOk = (int)args![3]! > 1000;
                _fixture.Events.Add(isOk ? "ok" : "input");
                if (!_fixture.KeepModalAfterTap)
                    _fixture.Frame = isOk ? _fixture.AfterOkFrame ?? Blank() : Blank();
                if (!isOk)
                {
                    var x = (int)args![2]!;
                    _fixture.InputXs.Add(x);
                    _fixture.AfterInput?.Invoke(x);
                }
                return Task.FromResult(new AdbCommandResult("", "", 0, false, null));
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
