using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSettlementEntryTests
{
    [Fact]
    public async Task Complete_career_entry_and_finish_dialog_are_distinguished()
    {
        var root = FindWorkspaceRoot();
        var screens = Path.Combine(root, "resource", "hachimi", "ura", "screens");
        var captures = Path.Combine(root, "testdata", "hachimi", "ura", "captures");
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var entry = pack.ScreenProfile.Find("complete_career_entry");
        var dialog = pack.ScreenProfile.Find("complete_career");
        Assert.NotNull(entry);
        Assert.NotNull(dialog);
        Assert.Equal(CareerScreenKind.Settlement,
            CareerScreenClassification.Classify(entry.ScreenId));
        Assert.Equal("complete_career_entry_open", entry.FindAction("open")?.Task);
        Assert.Equal("complete_career_career_finish", dialog.FindAction("finish")?.Task);

        var entryFrame = Load(Path.Combine(captures, "complete_career_entry.png"));
        var dialogFrame = Load(Path.Combine(screens, "templates", "runtime_frames",
            "ura_complete_career_next.png"));
        var entryTemplate = Load(Path.Combine(screens,
            entry.Recognition.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var dialogTemplate = Load(Path.Combine(screens,
            dialog.Recognition.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var entryMatch = Match(entryFrame, entryTemplate, entry.Recognition);
        Assert.True(entryMatch.Found, $"Entry score {entryMatch.Score:0.000}.");
        Assert.InRange(entryMatch.CenterX, 65, 95);
        Assert.InRange(entryMatch.CenterY, 10, 40);
        Assert.False(Match(entryFrame, dialogTemplate, dialog.Recognition).Found);
        Assert.False(Match(dialogFrame, entryTemplate, entry.Recognition).Found);
        Assert.True(Match(dialogFrame, dialogTemplate, dialog.Recognition).Found);

        var open = pack.ExecutionDefinition.GetTask("complete_career_entry_open");
        Assert.Equal("ClickSelf", open.Action);
        Assert.Equal("MatchTemplateColor", open.Algorithm);
        var actionTemplate = Load(Path.Combine(screens,
            open.Template!.Replace('/', Path.DirectorySeparatorChar)));
        var actionMatch = TemplateMatcher.FindColor(entryFrame, actionTemplate,
            open.Roi, open.TemplateThreshold, 900, 1600);
        Assert.True(actionMatch.Found, $"Click score {actionMatch.Score:0.000}.");
        Assert.InRange(actionMatch.CenterX, 590, 690);
        Assert.InRange(actionMatch.CenterY, 1320, 1380);

        var trainingSelectionFrame = Load(Path.Combine(captures,
            "training_selection_turn15_ura.png"));
        Assert.False(Match(trainingSelectionFrame, entryTemplate, entry.Recognition).Found);
        Assert.False(TemplateMatcher.FindColor(trainingSelectionFrame, actionTemplate,
            open.Roi, open.TemplateThreshold, 900, 1600).Found);

        var rgba = actionTemplate.RgbaPixels!;
        for (var x = 0; x < actionTemplate.Width; x++)
        {
            Assert.Equal(0, rgba[(x * 4) + 3]);
            Assert.Equal(0, rgba[(((actionTemplate.Height - 1) * actionTemplate.Width + x) * 4) + 3]);
        }
        for (var y = 0; y < actionTemplate.Height; y++)
        {
            Assert.Equal(0, rgba[((y * actionTemplate.Width) * 4) + 3]);
            Assert.Equal(0, rgba[((y * actionTemplate.Width + actionTemplate.Width - 1) * 4) + 3]);
        }
    }

    private static TemplateMatchResult Match(
        GrayImage frame,
        GrayImage template,
        UraScreenRecognition recognition) =>
        TemplateMatcher.FindColor(frame, template, recognition.Roi,
            recognition.TemplateThreshold, 900, 1600,
            requireTextContrast: recognition.MatchColorText);

    private static GrayImage Load(string path) =>
        GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Could not load settlement image.", path);

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }
}
