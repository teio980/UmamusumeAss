using System.Text.Json.Nodes;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.ViewModels;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class TaskEditorTests
{
    private static TaskQueueItemViewModel Career(JsonObject? settings = null) => new(UmaTaskCatalog.Get("career-training"), true, settings ?? new());

    [Fact]
    public void SupportTypeCountsReplaceFixedPresetsAndSurviveReload()
    {
        var task = Career();
        var editor = task.SettingsEditor;
        editor.SupportDeckMode = "highest-star";
        Assert.Equal("by-type", editor.SelectedSupportDeckPreset!.Value);
        Assert.True(editor.IsSupportTypeComposition);
        Assert.Single(editor.SupportDeckPresets);
        editor.SupportStaminaCount = 0;
        Assert.False(editor.IsValid);
        editor.SupportPowerCount = 3;
        Assert.Equal("types-speed3-power3", task.Settings["supportDeckPreset"]!.GetValue<string>());
        Assert.Equal(6, editor.SupportTypeTotal);
        Assert.True(editor.IsValid, editor.ValidationMessage);

        var reloaded = Career(JsonNode.Parse(task.Settings.ToJsonString())!.AsObject()).SettingsEditor;
        Assert.Equal(3, reloaded.SupportSpeedCount);
        Assert.Equal(3, reloaded.SupportPowerCount);
        Assert.Equal(0, reloaded.SupportStaminaCount);
        Assert.Equal("by-type", reloaded.SelectedSupportDeckPreset!.Value);
        reloaded.SupportDeckMode = "auto";
        Assert.False(reloaded.IsSupportTypeComposition);
        reloaded.SupportDeckMode = "highest-star";
        Assert.Equal(3, reloaded.SupportPowerCount);
    }

    [Fact]
    public void BorrowedCardMustMatchARequestedTypeEvenWhenFriendTypeIsIncluded()
    {
        var editor = Career(new()
        {
            ["supportDeckMode"] = "highest-star",
            ["supportDeckPreset"] = "types-power5-friend1",
        }).SettingsEditor;
        var speedCard = editor.SupportCardOptions.First(card => card.Category == "Speed");
        editor.FriendSupportCardIdText = speedCard.Id;
        Assert.False(editor.IsValid);
        editor.FriendSupportCardIdText = editor.SupportCardOptions.First(card => card.Category == "Power").Id;
        Assert.True(editor.IsValid, editor.ValidationMessage);
    }

    [Fact]
    public void ManualCompositionCountsTheBorrowedCardWithinTheSixCards()
    {
        var editor = Career(new()
        {
            ["supportDeckMode"] = "selected",
            ["supportDeckPreset"] = "types-speed3-power3",
        }).SettingsEditor;
        var speed = editor.SupportCardOptions.Where(card => card.Category == "Speed").Take(3).ToArray();
        var power = editor.SupportCardOptions.Where(card => card.Category == "Power").Take(3).ToArray();
        editor.SupportCardIdsText = string.Join(", ", speed.Concat(power.Take(2)).Select(card => card.Id));
        editor.FriendSupportCardIdText = power[2].Id;
        Assert.True(editor.IsValid, editor.ValidationMessage);
        editor.FriendSupportCardIdText = editor.SupportCardOptions.First(card => card.Category == "Stamina").Id;
        Assert.False(editor.IsValid);
    }

    [Theory]
    [InlineData("speed3-stamina3", 3, 3, 0, 0)]
    [InlineData("speed3-stamina2-wit1", 3, 2, 0, 0)]
    [InlineData("speed2-stamina2-power1-wit1", 2, 2, 1, 0)]
    [InlineData("speed2-stamina1-power1-wit1-friend1", 2, 1, 1, 1)]
    public void ExistingSupportPresetsLoadAsEditableCounts(string preset, int speed, int stamina, int power, int friend)
    {
        var editor = Career(new() { ["supportDeckMode"] = "highest-star", ["supportDeckPreset"] = preset }).SettingsEditor;
        Assert.Equal("by-type", editor.SelectedSupportDeckPreset!.Value);
        Assert.Equal(speed, editor.SupportSpeedCount);
        Assert.Equal(stamina, editor.SupportStaminaCount);
        Assert.Equal(power, editor.SupportPowerCount);
        Assert.Equal(friend, editor.SupportFriendCount);
        Assert.Equal(6, editor.SupportTypeTotal);
    }

    [Theory]
    [InlineData("types-speed3-power3", true)]
    [InlineData("types-guts3-wit2-friend1", true)]
    [InlineData("types-speed6", true)]
    [InlineData("types-friend6", true)]
    [InlineData("types-speed0-power6", true)]
    [InlineData("types-speed3-power2", false)]
    [InlineData("types-speed6-power1", false)]
    [InlineData("types-speed3-speed3", false)]
    [InlineData("types-speed7", false)]
    [InlineData("types-speed3-unknown3", false)]
    public void SupportCompositionCodecRequiresSixCards(string value, bool valid)
    {
        var types = Umamusume.CoreBridge.SupportDeckComposition.Parse(value);
        Assert.Equal(valid, types is not null && types.Values.Sum() == 6);
    }

    [Fact]
    public void EditingKnownFieldsPreservesFutureProfileFields()
    {
        var task = Career(new() { ["futureSetting"] = new JsonObject { ["nested"] = "retain" }, ["normalTrainingRatio"] = new JsonObject { ["stamina"] = 7, ["futureWeight"] = 9 } });
        task.SettingsEditor.NormalTrainingSpeedText = "3";
        task.SettingsEditor.RunCountText = "4";
        Assert.Equal("retain", task.Settings["futureSetting"]!["nested"]!.GetValue<string>());
        Assert.Equal(7, task.Settings["normalTrainingRatio"]!["stamina"]!.GetValue<int>());
        Assert.Equal(9, task.Settings["normalTrainingRatio"]!["futureWeight"]!.GetValue<int>());
        Assert.Equal(3, task.Settings["normalTrainingRatio"]!["speed"]!.GetValue<int>());
        Assert.Equal(4, task.Settings["runCount"]!.GetValue<int>());
    }

    [Fact]
    public void IncompleteJsonDoesNotOverwriteLastValidTaskSettings()
    {
        var task = Career(new() { ["runCount"] = 2 });
        task.SettingsEditor.RawSettingsJson = "{\"runCount\":";
        Assert.True(task.SettingsEditor.HasJsonError);
        Assert.Equal(2, task.Settings["runCount"]!.GetValue<int>());
        task.SettingsEditor.RawSettingsJson = "{\"runCount\":3,\"future\":true}";
        Assert.False(task.SettingsEditor.HasJsonError);
        Assert.Equal(3, task.Settings["runCount"]!.GetValue<int>());
        Assert.True(task.Settings["future"]!.GetValue<bool>());
    }

    [Fact]
    public void CopiedTasksHaveIndependentSettingsAndEditorState()
    {
        var source = Career(new() { ["runCount"] = 2 });
        var copy = source.Clone();
        copy.SettingsEditor.RunCountText = "8";
        copy.IsEnabled = false;
        Assert.Equal(2, source.Settings["runCount"]!.GetValue<int>());
        Assert.True(source.IsEnabled);
        Assert.Equal(8, copy.Settings["runCount"]!.GetValue<int>());
    }

    [Fact]
    public void DeleteExistingCareerKeepsLegacyInverseFieldCompatible()
    {
        var task = Career();
        task.SettingsEditor.DeleteExistingCareerData = true;
        Assert.True(task.Settings["deleteExistingCareerData"]!.GetValue<bool>());
        Assert.False(task.Settings["continueExistingCareer"]!.GetValue<bool>());
        task.SettingsEditor.DeleteExistingCareerData = false;
        Assert.True(task.Settings["continueExistingCareer"]!.GetValue<bool>());
    }

    [Fact]
    public void ShopEditsPreserveOtherPagesSettings()
    {
        var settings = new MemorySettings();
        settings.Value.Theme = "Light";
        settings.Value.Hachimi.TaskQueue.Add(new() { TaskId = "career-training", Settings = new() { ["runCount"] = 3 } });
        var shop = new ShopTaskSettingsViewModel(settings);
        shop.BuyAlarmClock = true;
        Assert.True(settings.Value.Hachimi.Shop.BuyAlarmClock);
        Assert.Equal("Light", settings.Value.Theme);
        Assert.Equal(3, settings.Value.Hachimi.TaskQueue[0].Settings["runCount"]!.GetValue<int>());
    }

    [Fact]
    public void LegacySparksTwoWayBindingWorksCorrectly()
    {
        var task = Career();
        var editor = task.SettingsEditor;

        Assert.False(editor.SparkSpeed);
        editor.SparkSpeed = true;
        Assert.True(editor.SparkSpeed);
        Assert.Contains("speed", editor.LegacyAttributeSparksText);

        editor.SparkTurf = true;
        editor.SparkMedium = true;
        Assert.True(editor.SparkTurf);
        Assert.True(editor.SparkMedium);
        Assert.Contains("turf", editor.LegacyAptitudeSparksText);
        Assert.Contains("medium", editor.LegacyAptitudeSparksText);

        editor.SparkSpeed = false;
        Assert.False(editor.SparkSpeed);
        Assert.DoesNotContain("speed", editor.LegacyAttributeSparksText);
    }

    [Fact]
    public void TrainingRatioCalculationsAndCyclePreview()
    {
        var task = Career();
        var editor = task.SettingsEditor;

        editor.NormalTrainingSpeedText = "3";
        editor.NormalTrainingStaminaText = "2";
        editor.NormalTrainingPowerText = "0";
        editor.NormalTrainingGutsText = "0";
        editor.NormalTrainingWitText = "1";

        Assert.Equal(6, editor.TotalRatio);
        Assert.False(editor.IsRatioInvalid);
        Assert.Equal("50%", editor.SpeedPercentage);
        Assert.Equal("33.3%", editor.StaminaPercentage);
        Assert.Equal("16.7%", editor.WitPercentage);
        Assert.Contains("速度 (3)", editor.RatioCyclePreview);
        Assert.Contains("耐力 (2)", editor.RatioCyclePreview);
        Assert.Contains("智力 (1)", editor.RatioCyclePreview);

        editor.NormalTrainingSpeedText = "0";
        editor.NormalTrainingStaminaText = "0";
        editor.NormalTrainingWitText = "0";
        Assert.Equal(0, editor.TotalRatio);
        Assert.True(editor.IsRatioInvalid);
        Assert.Equal("—", editor.RatioCyclePreview);
    }

    [Fact]
    public void SupportDeckAddAndRemove()
    {
        var task = Career();
        var editor = task.SettingsEditor;

        var sampleCard = new Services.SupportCardCatalogItem(10001, "Special Week", "Speed", "SSR", "path") { IsAvailable = true };
        editor.AddSupportCard(sampleCard);

        Assert.Contains("10001", editor.SupportCardIdsText);
        editor.RemoveSupportCard(sampleCard);
        Assert.DoesNotContain("10001", editor.SupportCardIdsText);
    }

    [Fact]
    public void SkillTargetPriorityReordering()
    {
        var task = Career();
        var editor = task.SettingsEditor;

        var skill1 = new Services.SkillCatalogItem(20001, "Skill A", "Normal", 120, "Effect")
        {
            AvailableInGlobal = true,
            SingleModeEnabled = true,
            NeedSkillPoint = 120
        };
        var skill2 = new Services.SkillCatalogItem(20002, "Skill B", "Normal", 150, "Effect")
        {
            AvailableInGlobal = true,
            SingleModeEnabled = true,
            NeedSkillPoint = 150
        };

        editor.SelectedAvailableNormalSkill = skill1;
        editor.AddSelectedNormalSkillCommand.Execute(null);
        editor.SelectedAvailableNormalSkill = skill2;
        editor.AddSelectedNormalSkillCommand.Execute(null);

        Assert.Equal("20001, 20002", editor.NormalSkillIdsText);
        Assert.Equal(2, editor.TargetNormalSkills.Count);

        // Move Skill B up
        editor.SelectedTargetNormalSkill = editor.TargetNormalSkills[1];
        editor.MoveNormalSkillUpCommand.Execute(null);
        Assert.Equal("20002, 20001", editor.NormalSkillIdsText);

        // Move Skill B down
        editor.SelectedTargetNormalSkill = editor.TargetNormalSkills[0];
        editor.MoveNormalSkillDownCommand.Execute(null);
        Assert.Equal("20001, 20002", editor.NormalSkillIdsText);

        // Remove Skill A
        editor.SelectedTargetNormalSkill = editor.TargetNormalSkills[0];
        editor.RemoveSelectedNormalSkillCommand.Execute(null);
        Assert.Equal("20002", editor.NormalSkillIdsText);
    }

    [Fact]
    public void HachimiTaskLogExecutionTracking()
    {
        var log = new HachimiTaskLogViewModel();
        var tasks = new List<TaskQueueItemViewModel>
        {
            Career(new() { ["runCount"] = 1 })
        };

        log.BeginRun(tasks);
        Assert.Equal("运行中", log.RunStatus);
        Assert.Single(log.Tasks);
        Assert.True(log.Tasks[0].IsWaiting);

        log.OnQueueEvent(new UmaTaskQueueEvent(
            0,
            "career-training",
            UmaTaskQueueEventKind.Started,
            TaskName: "Career",
            Message: "Starting turn 1"
        ), tasks);

        Assert.True(log.Tasks[0].IsRunning);
        Assert.Single(log.Tasks[0].Entries);
        Assert.Equal("任务开始运行", log.Tasks[0].Entries[0].Message);
        log.Clear();
    }

    private sealed class MemorySettings : ISettingsService
    {
        public ConnectionSettings Value { get; private set; } = new();
        public ConnectionSettings Load() => Value;
        public void Save(ConnectionSettings settings) => Value = settings;
    }

    [Fact]
    public void TestComboBoxWithTaskTypeOption()
    {
        var options = TaskTypeOption.Build("zh-CN");
        var combo = new global::Avalonia.Controls.ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0
        };
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Equal(options[0], combo.SelectedItem);
    }
}
