using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentSkillPickerRecoveryTests
{
    [Fact]
    public async Task Two_skills_each_reopen_search_select_confirm_and_recover_main_add_button()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json"));

        Assert.NotNull(definition);
        var open = definition!.GetTask("independent_skills_open");
        var openVerify = definition.GetTask("independent_skills_picker_open_verify");
        var openScroll = definition.GetTask("independent_skills_open_scroll");
        var postConfirm = definition.GetTask("independent_skills_post_confirm");
        var postConfirmScroll = definition.GetTask("independent_skills_post_confirm_scroll");
        Assert.Null(open.Swipe);
        Assert.Equal([300, 500, 300, 800], open.Roi!);
        Assert.Equal(["independent_skills_picker_open_verify"], open.Next);
        Assert.Equal(["independent_skills_open_scroll"], open.OnErrorNext);
        Assert.Equal([600, 1318, 60, 60], openVerify.Roi!);
        Assert.True(openVerify.Success);
        Assert.Equal([450, 1100, 450, 800, 300], openScroll.Swipe!);
        Assert.Equal(1, openScroll.MaxTimes);
        Assert.Equal(["independent_skills_open"], openScroll.Next);
        Assert.Null(postConfirm.Swipe);
        Assert.Equal([300, 500, 300, 800], postConfirm.Roi!);
        Assert.Equal(["independent_skills_post_confirm_scroll"], postConfirm.OnErrorNext);
        Assert.Equal([450, 1100, 450, 800, 300], postConfirmScroll.Swipe!);
        Assert.Equal(1, postConfirmScroll.MaxTimes);
        Assert.Equal(["independent_skills_post_confirm"], postConfirmScroll.Next);

        var pack = await UraScenarioPackLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"));
        Assert.NotNull(pack);
        Assert.True(
            AdbCareerTrainingPipeline.TryValidateIndependentTemplateAction(
                pack!,
                "independent.skills.post.confirm",
                out var mappingError),
            mappingError);

        var visual = new SkillPickerVisualRuntime();
        var runner = new HachimiJsonPipelineRunner(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(),
                $"independent-skill-picker-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

        for (var skill = 0; skill < 2; skill++)
        {
            Assert.True(
                (await runner.RunAsync(
                    connection,
                    definition!,
                    "independent_skills_open")).Succeeded);
            Assert.True(
                (await runner.RunAsync(
                    connection,
                    definition!,
                    "independent_skills_search_checkbox")).Succeeded);
            Assert.True(
                (await runner.RunAsync(
                    connection,
                    definition!,
                    "independent_skills_save")).Succeeded);

            // Confirm has closed the picker and inserted a row. The fake
            // runtime hides Add Skills until the recovery probe scrolls the
            // expanded main section back into view.
            var recovered = await runner.RunAsync(
                connection,
                definition!,
                "independent_skills_post_confirm");
            Assert.True(recovered.Succeeded, recovered.Message);
        }

        Assert.Equal(2, visual.ConfirmedSkills);
        Assert.Equal(
            [
                "independent_skills_open",
                "independent_skills_search_checkbox",
                "independent_skills_save",
                "independent_skills_open",
                "independent_skills_search_checkbox",
                "independent_skills_save",
            ],
            visual.TappedTaskNames);
        Assert.Equal(
            [
                "independent_skills_post_confirm_scroll",
                "independent_skills_post_confirm_scroll",
            ],
            visual.SwipeTaskNames);
        Assert.All(
            visual.SwipeCoordinates,
            coordinates => Assert.Equal([450, 1100, 450, 800, 300], coordinates));

        Assert.Equal(
            [
                "independent_skills_open",
                "independent_skills_picker_open_verify",
                "independent_skills_search_checkbox",
                "independent_skills_save",
                "independent_skills_post_confirm",
                "independent_skills_post_confirm",
                "independent_skills_open",
                "independent_skills_picker_open_verify",
                "independent_skills_search_checkbox",
                "independent_skills_save",
                "independent_skills_post_confirm",
                "independent_skills_post_confirm",
            ],
            visual.WaitedTaskNames);
    }

    [Fact]
    public async Task Main_skill_reset_repeats_past_the_old_limit_until_every_row_is_gone()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json"));

        Assert.NotNull(definition);
        var resetProbe = definition!.GetTask("independent_skills_reset_probe");
        var resetClick = definition.GetTask("independent_skills_main_reset");
        Assert.Equal([400, 600, 360, 500], resetProbe.Roi!);
        Assert.Equal([400, 600, 360, 500], resetClick.Roi!);
        Assert.Equal(["independent_skills_main_reset"], resetProbe.Next);
        Assert.Equal(["independent_skills_reset_done"], resetProbe.OnErrorNext);
        Assert.Equal(["independent_skills_reset_probe"], resetClick.Next);
        Assert.Equal(0, resetClick.MaxTimes);
        Assert.Equal(["independent_skills_reset_exceeded"], resetClick.ExceededNext);

        var visual = new SkillPickerVisualRuntime(residualSkills: 25);
        var runner = new HachimiJsonPipelineRunner(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(),
                $"independent-skill-reset-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

        var result = await runner.RunAsync(
            connection,
            definition,
            "independent_skills_reset_probe");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(25, visual.ResetClicks);
        Assert.Equal(26, visual.ResetProbeCount);
        Assert.Equal(0, visual.ResidualSkills);
        Assert.All(
            visual.TappedTaskNames,
            taskName => Assert.Equal("independent_skills_main_reset", taskName));
    }

    [Fact]
    public async Task Main_skill_reset_stops_when_reset_click_limit_is_exceeded()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json"));

        Assert.NotNull(definition);
        definition!.GetTask("independent_skills_main_reset").MaxTimes = 2;

        var visual = new SkillPickerVisualRuntime(residualSkills: 3);
        var runner = new HachimiJsonPipelineRunner(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(),
                $"independent-skill-reset-limit-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

        var result = await runner.RunAsync(
            connection,
            definition,
            "independent_skills_reset_probe");

        Assert.False(result.Succeeded);
        Assert.Contains("requested pipeline stop", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, visual.ResetClicks);
        Assert.Equal(3, visual.ResetProbeCount);
        Assert.Equal(1, visual.ResidualSkills);
    }

    [Fact]
    public async Task Main_skill_reset_uses_button_disappearance_as_the_only_completion_condition()
    {
        var root = FindSolutionRoot();
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json"));

        Assert.NotNull(definition);

        var visual = new SkillPickerVisualRuntime(
            residualSkills: 3,
            returnUnchangedScreenshot: true);
        var runner = new HachimiJsonPipelineRunner(
            new AdbRuntime(new NoOpAdbRunner(), new AsyncDelay()),
            visual,
            new JsonSettingsService(Path.Combine(
                Path.GetTempPath(),
                $"independent-skill-reset-no-change-{Guid.NewGuid():N}.json")));
        var connection = new LastVerifiedConnection(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

        var result = await runner.RunAsync(
            connection,
            definition!,
            "independent_skills_reset_probe");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, visual.ResetClicks);
        Assert.Equal(4, visual.ResetProbeCount);
        Assert.Equal(0, visual.ResidualSkills);
    }

    private sealed class SkillPickerVisualRuntime : IVisualPipelineRuntime
    {
        private bool _pickerOpen;
        private bool _addVisible = true;
        private bool _skillSelected;
        private int _residualSkills;
        private readonly bool _returnUnchangedScreenshot;

        public SkillPickerVisualRuntime(
            int residualSkills = 0,
            bool returnUnchangedScreenshot = false)
        {
            _residualSkills = Math.Max(0, residualSkills);
            _returnUnchangedScreenshot = returnUnchangedScreenshot;
        }

        public int ConfirmedSkills { get; private set; }

        public int ResetClicks { get; private set; }

        public int ResetProbeCount { get; private set; }

        public int ResidualSkills => _residualSkills;

        public List<string> TappedTaskNames { get; } = [];

        public List<string> SwipeTaskNames { get; } = [];

        public List<int[]> SwipeCoordinates { get; } = [];

        public List<string> WaitedTaskNames { get; } = [];

        public Task<GrayImage?> CaptureGrayAsync(
            LastVerifiedConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GrayImage?>(_returnUnchangedScreenshot
                ? new GrayImage(1, 1, [128])
                : null);

        public Task<GrayImage?> LoadTemplateAsync(
            string? templatePath,
            string baseDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GrayImage?>(null);

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
            WaitedTaskNames.Add(taskName);
            var found = taskName switch
            {
                "independent_skills_open" => !_pickerOpen && _addVisible,
                "independent_skills_picker_open_verify" => _pickerOpen,
                "independent_skills_post_confirm" => !_pickerOpen && _addVisible,
                "independent_skills_reset_probe" => _residualSkills > 0,
                "independent_skills_main_reset" => _residualSkills > 0,
                "independent_skills_search_checkbox" => _pickerOpen,
                "independent_skills_save" => _pickerOpen && _skillSelected,
                _ => false,
            };
            if (taskName.Equals("independent_skills_reset_probe", StringComparison.OrdinalIgnoreCase))
                ResetProbeCount++;
            return Task.FromResult<TemplateMatchResult?>(
                new(found, found ? 1d : 0d, 35, 900, 830, 125));
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
            TappedTaskNames.Add(taskName);
            switch (taskName)
            {
                case "independent_skills_open":
                    _pickerOpen = true;
                    _addVisible = false;
                    _skillSelected = false;
                    break;
                case "independent_skills_search_checkbox":
                    _skillSelected = true;
                    break;
                case "independent_skills_save":
                    _pickerOpen = false;
                    _skillSelected = false;
                    _addVisible = false;
                    ConfirmedSkills++;
                    break;
                case "independent_skills_main_reset":
                    ResetClicks++;
                    if (_residualSkills > 0)
                        _residualSkills--;
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
            CancellationToken cancellationToken = default)
        {
            SwipeTaskNames.Add(taskName);
            SwipeCoordinates.Add([.. coordinates]);
            if (taskName.Equals("independent_skills_post_confirm_scroll", StringComparison.OrdinalIgnoreCase)
                || taskName.Equals("independent_skills_open_scroll", StringComparison.OrdinalIgnoreCase))
                _addVisible = true;
            return Task.CompletedTask;
        }

        public Task SaveScreenshotAsync(
            LastVerifiedConnection connection,
            string definitionPath,
            string name,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DelayAsync(
            int milliseconds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpAdbRunner : IAdbRunner
    {
        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(
            string adbPath) =>
            (string.Empty, string.Empty, 0, false, null);
    }

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
}
