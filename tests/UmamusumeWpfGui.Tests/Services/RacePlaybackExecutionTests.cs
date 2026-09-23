using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class RacePlaybackExecutionTests
{
    [Fact]
    public async Task Flashing_view_results_prompt_retries_until_result_page_is_verified()
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);
        definition!.GetTask("race_runner_view_results_tap").TimeoutMilliseconds = 500;

        var tapPrompt = ComposeFrame(root, "tap_prompt",
            new Overlay("templates/career/race/race_view_results_tap.png", 400, 1200));
        var replay = ComposeFrame(root, "replay",
            new Overlay("templates/career/race/race_result_next.png", 240, 1380));
        var lastNext = ComposeFrame(root, "last_next",
            new Overlay("templates/career/race/race_last_next.png", 450, 1400));
        var visual = new RacePlaybackVisualRuntime(
            root, tapPrompt, tapPrompt, tapPrompt, replay, lastNext,
            [], ignoreFirstPlaybackStartTap: false,
            cancelSourceOnFirstCapture: null,
            ignoreFirstViewResultsTap: true);

        var result = await CreateRunner(visual).RunAsync(
            CreateConnection(), definition!, "race_runner_view_results_tap");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, visual.ClickedTaskNames.Count(name =>
            name == "race_runner_view_results_tap"));
        Assert.Equal("race_runner_result_next", visual.ClickedTaskNames[^2]);
        Assert.Equal("race_runner_last_next", visual.ClickedTaskNames[^1]);
    }

    [Fact]
    public async Task Playback_waits_through_loading_retries_central_race_then_skips_replays_and_finishes()
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);

        var visual = CreateNormalVisual(
            root,
            ignoreFirstPlaybackStartTap: true,
            startFromRunner: true);
        var result = await CreateRunner(visual).RunAsync(
            CreateConnection(),
            definition!,
            "race_runner_entry_start");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            [
                "race_runner_entry_start",
                "race_runner_playback_ok",
                "race_runner_playback_resume_race",
                "race_runner_playback_start",
                "race_runner_playback_start",
                "race_runner_playback_skip",
                "race_runner_result_next",
                "race_runner_last_next",
            ],
            visual.ClickedTaskNames);
        string[] capturePrefix =
        [
            "ready_runner_race",
            "ready_loading",
            "ready_central_race",
            "result_race_still_visible",
            "result_loading",
            "result_skip",
            "result_replay",
        ];
        Assert.Equal(capturePrefix, visual.CapturedStates.Take(capturePrefix.Length));
        Assert.NotEmpty(visual.CapturedStates.Skip(capturePrefix.Length));
        Assert.All(
            visual.CapturedStates.Skip(capturePrefix.Length),
            state => Assert.Equal("steady", state));
        Assert.Contains("race_runner_playback_start", visual.ColorWaitedTaskNames);
        Assert.Contains("race_runner_playback_ok", visual.WaitedTaskNames);
        Assert.Contains(
            visual.LoadedTemplateNames,
            name => name.Equals("race_playback_start", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("result_replay")]
    [InlineData("result_replay_and_skip")]
    public async Task Playback_result_monitor_prioritizes_a_direct_result(string resultState)
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);

        var visual = CreateNormalVisual(
            root,
            ignoreFirstPlaybackStartTap: false,
            resultCaptureStates: [resultState]);
        var result = await CreateRunner(visual).RunAsync(
            CreateConnection(),
            definition!,
            "race_runner_playback_ok");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            [
                "race_runner_playback_ok",
                "race_runner_playback_start",
                "race_runner_result_next",
                "race_runner_last_next",
            ],
            visual.ClickedTaskNames);
        Assert.DoesNotContain("race_runner_playback_skip", visual.ClickedTaskNames);
        Assert.DoesNotContain(
            "race_runner_playback_resume_race",
            visual.ClickedTaskNames);
        string[] capturePrefix = ["ready_loading", "ready_central_race", resultState];
        Assert.Equal(capturePrefix, visual.CapturedStates.Take(capturePrefix.Length));
        Assert.NotEmpty(visual.CapturedStates.Skip(capturePrefix.Length));
        Assert.All(
            visual.CapturedStates.Skip(capturePrefix.Length),
            state => Assert.Equal("steady", state));
    }

    [Fact]
    public async Task Playback_closes_delayed_trophy_then_continues_replay_result_flow()
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);

        var visual = CreateNormalVisual(
            root,
            ignoreFirstPlaybackStartTap: false,
            resultCaptureStates: ["result_replay", "result_trophy"]);
        var result = await CreateRunner(visual).RunAsync(
            CreateConnection(),
            definition,
            "race_runner_playback_ok");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            [
                "race_runner_playback_ok",
                "race_runner_playback_start",
                "race_runner_trophy_close",
                "race_runner_result_next",
                "race_runner_last_next",
            ],
            visual.ClickedTaskNames);
        Assert.DoesNotContain("race_runner_playback_skip", visual.ClickedTaskNames);
        Assert.Equal(
            ["ready_loading", "ready_central_race", "result_replay", "result_trophy"],
            visual.CapturedStates);
    }

    [Fact]
    public async Task Playback_monitor_propagates_cancellation_while_loading()
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);

        using var cancellation = new CancellationTokenSource();
        var visual = CreateNormalVisual(
            root,
            ignoreFirstPlaybackStartTap: true,
            cancelSourceOnFirstCapture: cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateRunner(visual).RunAsync(
            CreateConnection(),
            definition!,
            "race_runner_playback_ready_monitor",
            cancellationToken: cancellation.Token));

        Assert.Equal(["ready_loading"], visual.CapturedStates);
        Assert.Empty(visual.ClickedTaskNames);
    }

    [Fact]
    public async Task Playback_monitor_times_out_without_a_result_or_monitor_button()
    {
        var root = FindSolutionRoot();
        var definition = await LoadDefinitionAsync(root);
        Assert.NotNull(definition);
        definition!.GetTask("race_runner_playback_result_monitor").TimeoutMilliseconds = 1;

        var visual = CreateNormalVisual(
            root,
            ignoreFirstPlaybackStartTap: true,
            resultCaptureStates: ["result_loading"]);
        var result = await CreateRunner(visual).RunAsync(
            CreateConnection(),
            definition,
            "race_runner_playback_result_monitor");

        Assert.False(result.Succeeded);
        Assert.Contains("Timed out waiting for parallel monitor", result.Message);
        Assert.Empty(visual.ClickedTaskNames);
    }

    private static RacePlaybackVisualRuntime CreateNormalVisual(
        string root,
        bool ignoreFirstPlaybackStartTap,
        CancellationTokenSource? cancelSourceOnFirstCapture = null,
        IReadOnlyList<string>? resultCaptureStates = null,
        bool startFromRunner = false)
    {
        var initial = ComposeFrame(
            root,
            "initial_playback_confirmation",
            new Overlay("templates/career/race/race_playback_ok.png", 430, 950));
        var empty = ComposeFrame(root, "loading", []);
        var runner = ComposeFrame(
            root,
            "runner",
            new Overlay("templates/career/race/race_entry_race.png", 546, 1450));
        var centralRace = ComposeFrame(
            root,
            "central_race",
            new Overlay("templates/career/race/race_playback_start.png", 395, 1445));
        var skip = ComposeFrame(
            root,
            "skip",
            new Overlay("templates/career/race/race_playback_skip.png", 630, 1440));
        var replay = ComposeFrame(
            root,
            "replay",
            new Overlay("templates/career/race/race_result_replay.png", 680, 520),
            new Overlay("templates/career/race/race_result_next.png", 240, 1380));
        var lastNext = ComposeFrame(
            root,
            "last_next",
            new Overlay("templates/career/race/race_last_next.png", 450, 1400));
        var replayWithSkip = ComposeFrame(
            root,
            "replay_and_skip",
            new Overlay("templates/career/race/race_result_replay.png", 680, 520),
            new Overlay("templates/career/race/race_result_next.png", 240, 1380),
            new Overlay("templates/career/race/race_playback_skip.png", 630, 1440));
        var trophy = ComposeFrame(
            root,
            "trophy",
            new Overlay("templates/career/race/race_trophy_won.png", 140, 330),
            new Overlay("templates/career/race/race_trophy_close.png", 398, 1120));

        var resultStates = resultCaptureStates ??
        [
            "result_race_still_visible",
            "result_loading",
            "result_skip",
            "result_replay",
        ];
        var stateFrames = new Dictionary<string, ScreenFrame>(StringComparer.OrdinalIgnoreCase)
        {
            ["result_race_still_visible"] = new("result_race_still_visible", centralRace),
            ["result_loading"] = new("result_loading", empty),
            ["result_skip"] = new("result_skip", skip),
            ["result_replay"] = new("result_replay", replay),
            ["result_replay_and_skip"] = new("result_replay_and_skip", replayWithSkip),
            ["result_trophy"] = new("result_trophy", trophy),
        };

        var captures = new List<ScreenFrame>
        {
            new("ready_loading", empty),
            new("ready_central_race", centralRace),
        };
        if (startFromRunner)
            captures.Insert(0, new ScreenFrame("ready_runner_race", runner));
        captures.AddRange(resultStates.Select(state =>
            stateFrames.TryGetValue(state, out var frame)
                ? frame
                : throw new ArgumentException($"Unknown synthetic playback state '{state}'.", nameof(resultCaptureStates))));

        return new RacePlaybackVisualRuntime(
            root,
            startFromRunner ? runner : initial,
            initial,
            empty,
            replay,
            lastNext,
            captures,
            ignoreFirstPlaybackStartTap,
            cancelSourceOnFirstCapture);
    }

    private static GrayImage ComposeFrame(
        string root,
        string name,
        params Overlay[] overlays)
    {
        const int width = 900;
        const int height = 1600;
        const byte background = 7;
        var pixels = new byte[width * height];
        Array.Fill(pixels, background);
        var rgba = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            var offset = index * 4;
            rgba[offset] = background;
            rgba[offset + 1] = background;
            rgba[offset + 2] = background;
            rgba[offset + 3] = byte.MaxValue;
        }

        foreach (var overlay in overlays)
        {
            var templatePath = Path.Combine(
                root,
                "resource",
                "hachimi",
                "ura",
                "screens",
                overlay.TemplatePath.Replace('/', Path.DirectorySeparatorChar));
            var template = GrayImageCodec.FromFile(templatePath)
                ?? throw new InvalidOperationException($"Could not load test template '{templatePath}'.");
            if (overlay.X < 0
                || overlay.Y < 0
                || overlay.X + template.Width > width
                || overlay.Y + template.Height > height)
            {
                throw new ArgumentOutOfRangeException(nameof(overlays), overlay, name);
            }

            for (var y = 0; y < template.Height; y++)
            {
                Buffer.BlockCopy(
                    template.Pixels,
                    y * template.Width,
                    pixels,
                    ((overlay.Y + y) * width) + overlay.X,
                    template.Width);
                if (template.RgbaPixels is not { } templateRgba)
                    continue;

                Buffer.BlockCopy(
                    templateRgba,
                    y * template.Width * 4,
                    rgba,
                    (((overlay.Y + y) * width) + overlay.X) * 4,
                    template.Width * 4);
            }
        }

        return new GrayImage(width, height, pixels, rgba);
    }

    private static async Task<HachimiPipelineDefinition?> LoadDefinitionAsync(string root) =>
        await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json"));

    private static HachimiJsonPipelineRunner CreateRunner(
        RacePlaybackVisualRuntime visual) =>
        new(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(),
                $"race-playback-execution-{Guid.NewGuid():N}.json")));

    private static LastVerifiedConnection CreateConnection() =>
        new(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record Overlay(string TemplatePath, int X, int Y);

    private sealed record ScreenFrame(string Name, GrayImage Image);

    private sealed class RacePlaybackVisualRuntime : IVisualPipelineRuntime
    {
        private readonly string _templateBaseDirectory;
        private readonly Queue<ScreenFrame> _captures;
        private readonly GrayImage _emptyFrame;
        private readonly GrayImage _replayFrame;
        private readonly GrayImage _confirmationFrame;
        private readonly GrayImage _lastNextFrame;
        private readonly bool _ignoreFirstPlaybackStartTap;
        private readonly bool _ignoreFirstViewResultsTap;
        private readonly CancellationTokenSource? _cancelSourceOnFirstCapture;
        private GrayImage _currentFrame;
        private bool _ignoredFirstPlaybackStartTap;
        private bool _ignoredFirstViewResultsTap;
        private bool _cancelledFirstCapture;

        public RacePlaybackVisualRuntime(
            string root,
            GrayImage initialFrame,
            GrayImage confirmationFrame,
            GrayImage emptyFrame,
            GrayImage replayFrame,
            GrayImage lastNextFrame,
            IEnumerable<ScreenFrame> captures,
            bool ignoreFirstPlaybackStartTap,
            CancellationTokenSource? cancelSourceOnFirstCapture,
            bool ignoreFirstViewResultsTap = false)
        {
            _templateBaseDirectory = Path.Combine(
                root,
                "resource",
                "hachimi",
                "ura",
                "screens");
            _currentFrame = initialFrame;
            _confirmationFrame = confirmationFrame;
            _emptyFrame = emptyFrame;
            _replayFrame = replayFrame;
            _lastNextFrame = lastNextFrame;
            _captures = new Queue<ScreenFrame>(captures);
            _ignoreFirstPlaybackStartTap = ignoreFirstPlaybackStartTap;
            _ignoreFirstViewResultsTap = ignoreFirstViewResultsTap;
            _cancelSourceOnFirstCapture = cancelSourceOnFirstCapture;
        }

        public List<string> ClickedTaskNames { get; } = [];

        public List<string> CapturedStates { get; } = [];

        public List<string> LoadedTemplateNames { get; } = [];

        public List<string> WaitedTaskNames { get; } = [];

        public List<string> ColorWaitedTaskNames { get; } = [];

        public Task<GrayImage?> CaptureGrayAsync(
            LastVerifiedConnection connection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = _captures.Count > 0
                ? _captures.Dequeue()
                : new ScreenFrame("steady", _currentFrame);
            _currentFrame = frame.Image;
            CapturedStates.Add(frame.Name);
            if (_cancelSourceOnFirstCapture is not null && !_cancelledFirstCapture)
            {
                _cancelledFirstCapture = true;
                _cancelSourceOnFirstCapture.Cancel();
            }

            return Task.FromResult<GrayImage?>(_currentFrame);
        }

        public Task<GrayImage?> LoadTemplateAsync(
            string? templatePath,
            string baseDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(templatePath))
                return Task.FromResult<GrayImage?>(null);

            var fullPath = Path.IsPathRooted(templatePath)
                ? templatePath
                : Path.GetFullPath(Path.Combine(
                    string.IsNullOrWhiteSpace(baseDirectory)
                        ? _templateBaseDirectory
                        : baseDirectory,
                    templatePath));
            var template = GrayImageCodec.FromFile(fullPath);
            if (template is not null)
                LoadedTemplateNames.Add(Path.GetFileNameWithoutExtension(fullPath));
            return Task.FromResult<GrayImage?>(template);
        }

        public Task<TemplateMatchResult?> WaitForMatchAsync(
            LastVerifiedConnection connection,
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitedTaskNames.Add(taskName);
            return Task.FromResult(Match(templatePath, roi, threshold, referenceWidth, referenceHeight));
        }

        public Task<TemplateMatchResult?> WaitForColorMatchAsync(
            LastVerifiedConnection connection,
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ColorWaitedTaskNames.Add(taskName);
            return Task.FromResult(Match(
                templatePath,
                roi,
                threshold,
                referenceWidth,
                referenceHeight,
                useColor: true));
        }

        public Task<TemplateMatchResult?> WaitForMatchScaledAsync(
            LastVerifiedConnection connection,
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            IReadOnlyList<double> scaleCandidates,
            CancellationToken cancellationToken = default) =>
            WaitForMatchAsync(
                connection,
                templatePath,
                roi,
                threshold,
                referenceWidth,
                referenceHeight,
                timeoutMilliseconds,
                pollIntervalMilliseconds,
                taskName,
                baseDirectory,
                cancellationToken);

        public Task<TemplateMatchResult?> WaitForMatchInRoisAsync(
            LastVerifiedConnection connection,
            string? templatePath,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            string baseDirectory,
            IReadOnlyList<int[]> searchRois,
            double minimumScoreGap,
            CancellationToken cancellationToken = default) =>
            WaitForMatchAsync(
                connection,
                templatePath,
                null,
                threshold,
                referenceWidth,
                referenceHeight,
                timeoutMilliseconds,
                pollIntervalMilliseconds,
                taskName,
                baseDirectory,
                cancellationToken);

        public Task<ScreenTextRecognitionResult?> DetectTextAsync(
            LastVerifiedConnection connection,
            int[]? roi,
            int referenceWidth,
            int referenceHeight,
            string? language,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextRecognitionResult?>(null);

        public Task<ScreenTextQueryResult?> FindTextAsync(
            LastVerifiedConnection connection,
            string targetText,
            int[]? roi,
            double fuzzyThreshold,
            bool unique,
            int referenceWidth,
            int referenceHeight,
            string? language,
            string taskName,
            string? matchMode = null,
            int groupRowHeight = 0,
            int rowGap = 0,
            bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextQueryResult?>(null);

        public Task<ScreenTextQueryResult?> WaitForTextAsync(
            LastVerifiedConnection connection,
            string targetText,
            int[]? roi,
            double fuzzyThreshold,
            bool unique,
            int referenceWidth,
            int referenceHeight,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string? language,
            string taskName,
            string? matchMode = null,
            int groupRowHeight = 0,
            int rowGap = 0,
            bool requireAllTokens = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreenTextQueryResult?>(null);

        public Task TapTextAsync(
            LastVerifiedConnection connection,
            ScreenTextCandidate match,
            int[]? clickOffset,
            int[]? rowExpansion,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TapMatchAsync(
            LastVerifiedConnection connection,
            TemplateMatchResult match,
            string taskName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClickedTaskNames.Add(taskName);
            switch (taskName)
            {
                case "race_runner_entry_start":
                    _currentFrame = _confirmationFrame;
                    break;
                case "race_runner_playback_ok":
                    _currentFrame = _emptyFrame;
                    break;
                case "race_runner_playback_start":
                    if (_ignoreFirstPlaybackStartTap && !_ignoredFirstPlaybackStartTap)
                    {
                        _ignoredFirstPlaybackStartTap = true;
                    }
                    else
                    {
                        _currentFrame = _emptyFrame;
                    }
                    break;
                case "race_runner_playback_skip":
                    break;
                case "race_runner_trophy_close":
                    _currentFrame = _replayFrame;
                    break;
                case "race_runner_view_results_tap":
                    if (_ignoreFirstViewResultsTap && !_ignoredFirstViewResultsTap)
                        _ignoredFirstViewResultsTap = true;
                    else
                        _currentFrame = _replayFrame;
                    break;
                case "race_runner_result_next":
                    _currentFrame = _lastNextFrame;
                    break;
            }

            return Task.CompletedTask;
        }

        public Task TapAsync(
            LastVerifiedConnection connection,
            int x,
            int y,
            int referenceWidth,
            int referenceHeight,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<HsvColorProbeResult?> ProbeHsvAsync(
            LastVerifiedConnection connection,
            int centerXReference,
            int centerYReference,
            int[]? offsetReference,
            int radiusReference,
            int referenceWidth,
            int referenceHeight,
            double hueMin,
            double hueMax,
            double saturationMin,
            double saturationMax,
            double valueMin,
            double valueMax,
            double minimumMatchRatio,
            int timeoutMilliseconds,
            int pollIntervalMilliseconds,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<HsvColorProbeResult?>(null);

        public Task SwipeAsync(
            LastVerifiedConnection connection,
            int[] coordinates,
            int referenceWidth,
            int referenceHeight,
            string taskName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveScreenshotAsync(
            LastVerifiedConnection connection,
            string definitionPath,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            int milliseconds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private TemplateMatchResult? Match(
            string? templatePath,
            int[]? roi,
            double threshold,
            int referenceWidth,
            int referenceHeight,
            bool useColor = false)
        {
            if (string.IsNullOrWhiteSpace(templatePath))
                return null;

            var fullPath = Path.IsPathRooted(templatePath)
                ? templatePath
                : Path.GetFullPath(Path.Combine(_templateBaseDirectory, templatePath));
            var template = GrayImageCodec.FromFile(fullPath)
                ?? throw new InvalidOperationException($"Could not load test template '{fullPath}'.");
            return useColor
                ? TemplateMatcher.FindColor(
                    _currentFrame,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight)
                : TemplateMatcher.Find(
                    _currentFrame,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight);
        }
    }

    private sealed class NoOpAdbRunner : IAdbRunner
    {
        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(
            string adbPath) =>
            (string.Empty, string.Empty, 0, false, null);
    }
}
