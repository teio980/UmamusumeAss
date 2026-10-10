using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSkillPurchaseTests
{
    [Fact]
    public async Task Delayed_purchase_dialogs_are_confirmed_and_cached_before_the_race()
    {
        var run = await RunAsync();

        Assert.Null(run.Result);
        Assert.Equal(["skills.open", "race.open_list"], run.Actions);
        Assert.Equal([run.SkillId], run.State.NormalLearnedSkillIds);
        Assert.Equal([run.SkillId], run.Remembered);
        Assert.Equal(1, run.Replay.LearnTaps);
        Assert.True(run.Replay.OnRaceDay);
        Assert.Contains(run.Replay.Waits, wait => wait is
            { Asset: "career.skill.confirmation.title", Timeout: 5000, Threshold: 0.9 });
        Assert.Contains(run.Replay.Waits, wait => wait is
            { Asset: "career.skill.learned.title", Timeout: 5000, Threshold: 0.9 });
        Assert.Equal(["skills", "race", "race"], run.Replay.ReturnCaptures);
        Assert.Equal(["career.skill.back"], run.Replay.ReturnTaps);
        Assert.Empty(run.Replay.ReturnWaits);
        Assert.Equal(0, run.Replay.ReturnOcrCalls);
    }

    [Fact]
    public async Task Missing_initial_close_match_does_not_erase_a_confirmed_purchase()
    {
        var run = await RunAsync(missFirstClose: true);

        Assert.Null(run.Result);
        Assert.Equal([run.SkillId], run.State.NormalLearnedSkillIds);
        Assert.Equal([run.SkillId], run.Remembered);
        Assert.Equal(1, run.Replay.LearnTaps);
        Assert.Equal(2, run.Replay.CloseAttempts);
        Assert.Equal(1, run.Replay.CloseAttemptsWhenRemembered);
        Assert.True(run.Replay.OnRaceDay);
        Assert.Equal(["learned", "skills", "race", "race"], run.Replay.ReturnCaptures);
        Assert.Equal(["career.skill.learned.close", "career.skill.back"], run.Replay.ReturnTaps);
        Assert.Empty(run.Replay.ReturnWaits);
        Assert.Contains(run.Log.Messages, message => message.Contains("Extra Tank was learned;"));
        Assert.Contains("race.open_list", run.Actions);
    }

    [Fact]
    public async Task Missing_completion_evidence_is_not_cached_or_sent_to_the_race()
    {
        var run = await RunAsync(missingCompletion: true);

        Assert.NotNull(run.Result);
        Assert.False(run.Result.Succeeded);
        Assert.Equal(["skills.open"], run.Actions);
        Assert.Empty(run.State.NormalLearnedSkillIds);
        Assert.Empty(run.Remembered);
        Assert.Contains(run.Log.Messages, message =>
            message.Contains("the learning completion dialog did not appear"));
    }

    [Fact]
    public async Task Different_confirmation_name_never_presses_learn()
    {
        var run = await RunAsync(wrongConfirmationName: true);

        Assert.NotNull(run.Result);
        Assert.False(run.Result.Succeeded);
        Assert.Equal(0, run.Replay.LearnTaps);
        Assert.Empty(run.State.NormalLearnedSkillIds);
        Assert.Empty(run.Remembered);
        Assert.Equal(["skills.open"], run.Actions);
    }

    [Theory]
    [InlineData("learned", "career.skill.learned.close")]
    [InlineData("confirmation", "career.skill.confirmation.cancel")]
    [InlineData("exit", "career.skill.exit.ok")]
    [InlineData("skills", "career.skill.back")]
    public async Task Return_handles_the_visible_page_on_the_first_capture(
        string page, string firstTap)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = PurchaseReplay.Create(root, false, false, false, out var replay);
        replay.StartReturn(page);
        var context = ReturnContext(pack);

        var returned = await new CareerSkillVisualAdapter(runtime).ReturnToRaceDayAsync(context);

        Assert.True(returned);
        Assert.Equal(firstTap, replay.ReturnTaps[0]);
        Assert.Equal(1, replay.CapturesAtFirstReturnTap);
        Assert.Empty(replay.ReturnWaits);
        Assert.Equal(0, replay.ReturnOcrCalls);
        Assert.True(replay.OnRaceDay);
    }

    [Fact]
    public async Task Return_observes_delayed_navigation_without_sending_back_again()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = PurchaseReplay.Create(root, false, false, false, out var replay);
        replay.StartReturn("skills", delayedBackFrames: 4);

        var returned = await new CareerSkillVisualAdapter(runtime)
            .ReturnToRaceDayAsync(ReturnContext(pack));

        Assert.True(returned);
        Assert.Equal(["career.skill.back"], replay.ReturnTaps);
        Assert.Equal(1, replay.CapturesAtFirstReturnTap);
        Assert.Empty(replay.ReturnWaits);
        Assert.Equal(["race", "race"], replay.ReturnCaptures.TakeLast(2));
    }

    [Fact]
    public async Task Already_returned_race_day_requires_two_frames_and_no_taps()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = PurchaseReplay.Create(root, false, false, false, out var replay);
        replay.StartReturn("race");

        var returned = await new CareerSkillVisualAdapter(runtime)
            .ReturnToRaceDayAsync(ReturnContext(pack));

        Assert.True(returned);
        Assert.Equal(["race", "race"], replay.ReturnCaptures);
        Assert.Empty(replay.ReturnTaps);
        Assert.Empty(replay.ReturnWaits);
        Assert.Equal(0, replay.ReturnOcrCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_return_capture_is_retried_before_any_tap(bool throws)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = PurchaseReplay.Create(root, false, false, false, out var replay);
        replay.StartReturn("skills");
        replay.FailFirstReturnCapture(throws);

        var returned = await new CareerSkillVisualAdapter(runtime)
            .ReturnToRaceDayAsync(ReturnContext(pack));

        Assert.True(returned);
        Assert.Equal(["missing", "skills", "race", "race"], replay.ReturnCaptures);
        Assert.Equal(["career.skill.back"], replay.ReturnTaps);
        Assert.Equal(2, replay.CapturesAtFirstReturnTap);
        Assert.Empty(replay.ReturnWaits);
    }

    private static CareerFlowContext ReturnContext(UraScenarioPack pack) =>
        new(new LastVerifiedConnection("adb", "serial", "android", "version",
                900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack, true, null!, null!, "pace", new UraCareerSessionState { CareerStarted = true },
            new CareerObservation("race_day", 1), null, CancellationToken.None);

    [Theory]
    [InlineData("career_skill_learn_sample.png")]
    [InlineData("career_skill_selected_sample.png")]
    public async Task Skill_points_bar_does_not_count_as_learning_completion(string capture)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var resources = pack.VisualResources!;
        Assert.True(resources.TryGetRegion("career.skill.learned.title", out var region));
        Assert.NotNull(region);
        var frame = GrayImageCodec.FromFile(Path.Combine(root, "resource", "hachimi", "ura",
            "screens", "captures", capture));
        var template = GrayImageCodec.FromFile(resources.ResolveVisualResource("career.skill.learned.title"));
        Assert.NotNull(frame);
        Assert.NotNull(template);
        var match = TemplateMatcher.FindColor(frame, template, region.Roi,
            region.Threshold!.Value, 900, 1600);

        Assert.False(match.Found, $"Skill Points falsely matched completion at score {match.Score:0.000}.");
        Console.WriteLine($"{capture}: completion false-positive candidate score={match.Score:0.000}.");
    }

    private static async Task<PurchaseRun> RunAsync(bool missFirstClose = false,
        bool missingCompletion = false, bool wrongConfirmationName = false)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var skillId = IndependentTrainingCatalog.Load(root).Skills
            .Single(skill => skill.SkillName == "Extra Tank").SkillId;
        var runtime = PurchaseReplay.Create(root, missFirstClose, missingCompletion,
            wrongConfirmationName, out var replay);
        var actions = new RecordingActions();
        var remembered = new List<int>();
        var log = new RecordingLog();
        var state = new UraCareerSessionState { CareerStarted = true };
        var connection = new LastVerifiedConnection("adb", "serial", "android", "version",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var context = new CareerFlowContext(connection, pack, true, null!, null!, "pace", state,
            new CareerObservation("race_day", 1), log, CancellationToken.None,
            NormalSkillIds: [skillId], RememberNormalSkillAsync: id =>
            {
                replay.CloseAttemptsWhenRemembered = replay.CloseAttempts;
                replay.StartReturn();
                remembered.Add(id);
                return Task.CompletedTask;
            });

        var result = await new CareerRaceFlow(runtime, actions).HandleAsync(context);
        return new(result, state, skillId, actions.ActionIds, remembered, replay, log);
    }

    private sealed record PurchaseRun(CareerTrainingResult? Result, UraCareerSessionState State,
        int SkillId, List<string> Actions, List<int> Remembered, PurchaseReplay Replay, RecordingLog Log);

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> ActionIds { get; } = [];
        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context, string screenId,
            string actionId, HachimiPipelineRunOptions? options = null)
        {
            ActionIds.Add(actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    private sealed class RecordingLog : IGrassTaskLogSink
    {
        public List<string> Messages { get; } = [];
        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info) =>
            Messages.Add(details);
    }

    public sealed record MatchWait(string Asset, int Timeout, double Threshold);

    // Replay captured game pages through the real template matcher. OCR names
    // are supplied so these cases focus on purchase evidence and transitions.
    public class PurchaseReplay : DispatchProxy
    {
        private readonly Dictionary<string, GrayImage> _frames = [];
        private string _page = "skills";
        private bool _selected;
        private bool _missFirstClose;
        private bool _missingCompletion;
        private bool _wrongConfirmationName;
        private bool _returning;
        private bool _closeMatchedByWait;
        private int _delayedBackFrames;
        private bool _backPending;
        private bool? _firstCaptureFailure;
        public int LearnTaps { get; private set; }
        public int CloseAttempts { get; private set; }
        public int CloseAttemptsWhenRemembered { get; set; }
        public bool OnRaceDay => _page == "race";
        public List<MatchWait> Waits { get; } = [];
        public List<string> ReturnCaptures { get; } = [];
        public List<string> ReturnTaps { get; } = [];
        public List<MatchWait> ReturnWaits { get; } = [];
        public int ReturnOcrCalls { get; private set; }
        public int CapturesAtFirstReturnTap { get; private set; }

        public void StartReturn(string? page = null, int delayedBackFrames = 0)
        {
            _returning = true;
            _delayedBackFrames = delayedBackFrames;
            if (page is not null)
                _page = page;
        }

        public void FailFirstReturnCapture(bool throws) => _firstCaptureFailure = throws;

        public static IVisualPipelineRuntime Create(string root, bool missFirstClose,
            bool missingCompletion, bool wrongConfirmationName, out PurchaseReplay replay)
        {
            var runtime = Create<IVisualPipelineRuntime, PurchaseReplay>();
            replay = (PurchaseReplay)(object)runtime;
            replay._missFirstClose = missFirstClose;
            replay._missingCompletion = missingCompletion;
            replay._wrongConfirmationName = wrongConfirmationName;
            foreach (var (key, file) in new[]
            {
                ("skills", "career_skill_learn_sample.png"),
                ("selected", "career_skill_selected_sample.png"),
                ("confirmation", "career_skill_confirmation_sample.png"),
                ("learned", "career_skill_learned_sample.png"),
                ("exit", "career_skill_exit_confirmation_sample.png"),
                ("race", "career_skill_race_day_662_sample.png"),
            })
            {
                replay._frames[key] = GrayImageCodec.FromFile(Path.Combine(root, "resource",
                    "hachimi", "ura", "screens", "captures", file))
                    ?? throw new FileNotFoundException(file);
            }
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(Path.Combine(
                        (string)args![1]!, (string)args[0]!)));
                case "CaptureGrayAsync":
                    if (_returning && _firstCaptureFailure is { } shouldThrow)
                    {
                        _firstCaptureFailure = null;
                        ReturnCaptures.Add("missing");
                        if (shouldThrow)
                            throw new IOException("Dropped return screenshot.");
                        return Task.FromResult<GrayImage?>(null);
                    }
                    if (_backPending && _delayedBackFrames-- <= 0)
                    {
                        _page = "race";
                        _backPending = false;
                    }
                    if (_returning)
                        ReturnCaptures.Add(_page);
                    return Task.FromResult<GrayImage?>(_frames[_page]);
                case "DelayAsync":
                    return Task.CompletedTask;
                case "DetectTextAsync":
                    if (_returning)
                        ReturnOcrCalls++;
                    var task = (string)args![5]!;
                    ScreenTextDetection[] detections = task switch
                    {
                        "career.skill.points" => [Text("662", 780, 1160)],
                        "career.skill.price" => [Text("72", 700, 665)],
                        "career.skill.list" => [Text("Extra Tank", 160, 600)],
                        "career.skill.row_status" => _selected ? [Text("Obtained", 650, 665)] : [],
                        "career.skill.confirmation.name" =>
                            [Text(_wrongConfirmationName ? "Gourmand" : "Extra Tank", 220, 180)],
                        _ => [],
                    };
                    return Task.FromResult<ScreenTextRecognitionResult?>(
                        new(detections, "en-US", 900, 1600));
                case "WaitForColorMatchAsync":
                    return Task.FromResult(Match(args!));
                case "TapMatchAsync":
                    if (_returning)
                    {
                        if (ReturnTaps.Count == 0)
                            CapturesAtFirstReturnTap = ReturnCaptures.Count;
                        ReturnTaps.Add((string)args![2]!);
                    }
                    switch ((string)args![2]!)
                    {
                        case "career.skill.plus": _selected = true; break;
                        case "career.skill.confirm": _page = "confirmation"; break;
                        case "career.skill.confirmation.learn":
                            LearnTaps++;
                            _page = _missingCompletion ? "skills" : "learned";
                            break;
                        case "career.skill.learned.close":
                            if (!_closeMatchedByWait)
                                CloseAttempts++;
                            _closeMatchedByWait = false;
                            _page = "skills";
                            break;
                        case "career.skill.confirmation.cancel": _page = "skills"; break;
                        case "career.skill.exit.ok": _page = "race"; break;
                        case "career.skill.back":
                            if (_delayedBackFrames > 0)
                                _backPending = true;
                            else
                                _page = "race";
                            break;
                        default: throw new InvalidOperationException($"Unexpected tap: {args[2]}");
                    }
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected purchase call: {targetMethod?.Name}");
            }
        }

        private TemplateMatchResult? Match(object?[] args)
        {
            var asset = (string)args[8]!;
            var threshold = (double)args[3]!;
            var timeout = (int)args[6]!;
            Waits.Add(new(asset, timeout, threshold));
            if (_returning)
                ReturnWaits.Add(new(asset, timeout, threshold));
            if (asset == "career.skill.learned.close"
                && ++CloseAttempts == 1 && _missFirstClose)
                return null;
            var page = asset == "career.skill.confirm" && _page == "skills" && _selected
                ? "selected" : _page;
            var template = GrayImageCodec.FromFile((string)args[1]!);
            Assert.NotNull(template);
            var match = TemplateMatcher.FindColor(_frames[page], template, (int[]?)args[2],
                threshold, (int)args[4]!, (int)args[5]!);
            if (asset == "career.skill.learned.close")
                _closeMatchedByWait = match.Found;
            // Modal transitions take 1.25 seconds and can render at 0.94.
            // A 0.5-second recovery probe stays separate from this wait.
            if (asset is "career.skill.confirmation.title" or "career.skill.learned.title"
                && timeout != 500)
            {
                var score = Math.Min(match.Score, 0.94);
                return match with { Found = match.Found && timeout >= 1250 && score >= threshold,
                    Score = score };
            }
            return match;
        }

        private static ScreenTextDetection Text(string value, int x, int y) =>
            new(value, new ScreenTextRect(x, y, 120, 30));
    }
}
