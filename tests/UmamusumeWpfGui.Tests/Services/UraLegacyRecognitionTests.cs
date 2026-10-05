using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraLegacyRecognitionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task Cached_guest_replacement_waits_for_the_picker_and_the_borrow_modal(
        int stalePickerFrames)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, ParentOpeningRuntime>();
        var runtime = (ParentOpeningRuntime)(object)visual;
        runtime.Cached = true;
        runtime.CompleteFirstSlot = true;
        for (var index = 0; index < stalePickerFrames; index++)
            runtime.Frames.Enqueue(GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
                root, "legacy_cached_before_replacement_20261005.png"))!);
        foreach (var capture in new[] { "legacy_current_failure_20261005.png",
                     "legacy_change_regression_20261005.png",
                     "legacy_cached_before_replacement_20261005.png",
                     "legacy_confirmation_stuck_20261005.png", "legacy_borrow_confirmed_20261005.png" })
        {
            var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, capture));
            Assert.NotNull(frame);
            runtime.Frames.Enqueue(frame);
        }
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), "legacy-borrow-transition-test.json")));
        var settings = new IndependentTrainingSettings("manifest.json", 100602, true,
            [], "selected", "custom", null, "manual", true, false, ["Power"], []);
        await new UraLegacySelector(visual, runner).SelectAsync(
            new LastVerifiedConnection("adb", "serial", "android", "version",
                900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack.ExecutionDefinition, settings, null, pack.VisualResources);

        Assert.Contains("uraLegacy1SelectionCompletedBorrowConfirm", runtime.TappedTasks);
        Assert.DoesNotContain("uraLegacyGuests", runtime.TappedTasks);
        Assert.Contains("legacy_select_legacy2_open", runtime.RequestedTasks);
        Assert.Empty(runtime.Frames);
    }

    [Fact]
    public async Task Filter_tab_is_recognized_when_a_notification_badge_is_present()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var resources = pack.VisualResources!;
        var asset = resources.Assets["career.entry.legacy.legacy_display_filter_tab"];
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "legacy_owned_display_filter_regression_20261005.png"));
        var template = GrayImageCodec.FromFile(asset.Path);
        Assert.NotNull(frame);
        Assert.NotNull(template);
        var match = TemplateMatcher.Find(frame, template,
            resources.Regions["career.entry.legacy.display.filter_tab"].Roi,
            asset.Threshold!.Value, 900, 1600);
        Assert.True(match.Found, $"Filter tab score: {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 630, 700);
        Assert.InRange(match.CenterY, 150, 185);
    }

    [Theory]
    [InlineData("legacy_change_regression_20261005.png", true)]
    [InlineData("legacy_current_failure_20261005.png", true)]
    [InlineData("legacy_guests_unselected_20261005.png", false)]
    [InlineData("legacy_cached_before_replacement_20261005.png", false)]
    public async Task Selected_guest_tab_is_recognized_only_on_the_parent_picker(
        string capture,
        bool expected)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var resources = pack.VisualResources!;
        var asset = resources.Assets["career.entry.legacy.legacy_guests_tab_selected"];
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, capture));
        var template = GrayImageCodec.FromFile(asset.Path);
        Assert.NotNull(frame);
        Assert.NotNull(template);
        var match = TemplateMatcher.Find(frame, template,
            resources.Regions["career.entry.legacy.guests.tab"].Roi,
            asset.Threshold!.Value, 900, 1600);
        Assert.True(match.Found == expected,
            $"Guest tab on {capture}: score {match.Score:0.000}, expected {expected}.");
    }

    [Theory]
    [InlineData(true, "legacy_select_legacy1_clear_cached", false)]
    [InlineData(false, "legacy_select_legacy1_open", false)]
    [InlineData(true, "legacy_select_legacy1_clear_cached", true)]
    public async Task Opening_a_parent_uses_Change_for_cached_records_and_plus_for_empty_slots(
        bool cached,
        string expectedOpenTask,
        bool switchToGuests)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, ParentOpeningRuntime>();
        var runtime = (ParentOpeningRuntime)(object)visual;
        runtime.Cached = cached;
        runtime.Frames.Enqueue(GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, switchToGuests ? "legacy_guests_unselected_20261005.png"
                : "legacy_current_failure_20261005.png"))!);
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), "legacy-opening-test.json")));
        var settings = new IndependentTrainingSettings("manifest.json", 100602, true,
            [], "selected", "custom", null, "manual", true, false, ["Power"], []);
        var result = await new UraLegacySelector(visual, runner).SelectAsync(
            new LastVerifiedConnection("adb", "serial", "android", "version",
                900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack.ExecutionDefinition, settings, null, pack.VisualResources);

        // The fake picker stops at View Sparks, after the opening transition.
        Assert.False(result.Succeeded);
        Assert.Equal(switchToGuests ? new[] { expectedOpenTask, "uraLegacyGuests" }
            : new[] { expectedOpenTask }, runtime.TappedTasks);
        Assert.Contains("uraLegacyViewSparksOff", runtime.RequestedTasks);
        Assert.DoesNotContain(cached ? "legacy_select_legacy1_open"
            : "legacy_select_legacy1_clear_cached", runtime.RequestedTasks);
    }

    public class ParentOpeningRuntime : DispatchProxy
    {
        public bool Cached { get; set; }
        public bool CompleteFirstSlot { get; set; }
        public Queue<GrayImage> Frames { get; } = new();
        public List<string> RequestedTasks { get; } = [];
        public List<string> TappedTasks { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "WaitForMatchAsync":
                    var task = (string)args![8]!;
                    RequestedTasks.Add(task);
                    var found = task == "uraLegacy1CachedRecord" ? Cached
                        : task is "legacy_select_legacy1_clear_cached" or "legacy_select_legacy1_open";
                    if (CompleteFirstSlot)
                        found = task is not ("uraLegacy2CachedRecord" or "uraLegacyViewSparksOff"
                            or "uraLegacySortAscending" or "legacy_select_legacy2_open");
                    return Task.FromResult<TemplateMatchResult?>(
                        new TemplateMatchResult(found, found ? 1 : 0, 237, 1114, 87, 30));
                case "TapMatchAsync":
                    TappedTasks.Add((string)args![2]!);
                    return Task.CompletedTask;
                case "DelayAsync":
                    return Task.CompletedTask;
                case "CaptureGrayAsync":
                    return Task.FromResult<GrayImage?>(Frames.Dequeue());
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(
                        Path.Combine((string)args![1]!, (string)args[0]!)));
                case "TapAsync":
                    TappedTasks.Add((string)args![5]!);
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected visual call: {targetMethod?.Name}.");
            }
        }
    }

    public class UnexpectedAdbRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected ADB call: {targetMethod?.Name}.");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Cached_parent_detection_rejects_the_empty_second_slot(int slot, bool expected)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var resources = pack.VisualResources!;
        var asset = resources.Assets[$"career.entry.legacy.legacy{slot}_cached_record"];
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "legacy_borrow_confirmed_20261005.png"));
        var template = GrayImageCodec.FromFile(asset.Path);
        Assert.NotNull(frame);
        Assert.NotNull(template);
        var match = TemplateMatcher.Find(frame, template,
            resources.Regions[$"career.entry.legacy.cached{slot}"].Roi,
            asset.Threshold!.Value, 900, 1600);
        Assert.True(match.Found == expected,
            $"Legacy {slot}: score {match.Score:0.000}, expected {expected}.");
    }

    [Theory]
    [InlineData("title", "legacy_confirmation_stuck_20261005.png", true)]
    [InlineData("ok", "legacy_confirmation_stuck_20261005.png", true)]
    [InlineData("title", "legacy_borrow_confirmed_20261005.png", false)]
    [InlineData("ok", "legacy_borrow_confirmed_20261005.png", false)]
    public async Task Borrow_confirmation_templates_match_the_modal_and_reject_legacy_select(
        string control,
        string capture,
        bool expected)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var resources = pack.VisualResources!;
        var asset = resources.Assets[$"career.entry.legacy.legacy_borrow_confirmation_{control}"];
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, capture));
        var template = GrayImageCodec.FromFile(asset.Path);
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(frame, template,
            resources.Regions[$"career.entry.legacy.borrow_confirmation.{control}"].Roi,
            asset.Threshold!.Value, 900, 1600, candidateStepOverride: 1);
        Assert.True(match.Found == expected,
            $"{control} on {capture}: score {match.Score:0.000}, expected {expected}.");
        if (expected)
            Assert.InRange(match.Score, 0.99, 1.0);
    }

    [Theory]
    [InlineData("asc", "desc")]
    [InlineData("desc", "asc")]
    public async Task Guest_sort_controls_recognize_only_the_displayed_direction(
        string direction,
        string oppositeDirection)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var controls = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Career",
            $"legacy-guests-{direction}-controls.png"));
        Assert.NotNull(controls);
        var pixels = new byte[900 * 1600];
        for (var row = 0; row < controls.Height; row++)
            Array.Copy(controls.Pixels, row * controls.Width, pixels,
                (1130 + row) * 900, controls.Width);
        var frame = new GrayImage(900, 1600, pixels);
        var resources = pack.VisualResources!;
        var roi = resources.Regions["career.entry.legacy.sort"].Roi;

        TemplateMatchResult Match(string state)
        {
            var asset = resources.Assets[$"career.entry.legacy.legacy_sort_{state}"];
            var template = GrayImageCodec.FromFile(asset.Path);
            Assert.NotNull(template);
            return TemplateMatcher.Find(frame, template, roi, asset.Threshold!.Value,
                pack.ExecutionDefinition.ReferenceWidth,
                pack.ExecutionDefinition.ReferenceHeight);
        }

        var displayed = Match(direction);
        Assert.True(displayed.Found, $"{direction} score: {displayed.Score:0.000}.");
        Assert.InRange(displayed.CenterX, 750, 860);
        Assert.InRange(displayed.CenterY, 1210, 1270);
        var opposite = Match(oppositeDirection);
        Assert.False(opposite.Found, $"Opposite direction score: {opposite.Score:0.000}.");
    }
}
