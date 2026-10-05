using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraLegacyRecognitionTests
{
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
