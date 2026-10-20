using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Umamusume.AppCore.Platform;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class IndependentAgendaRaceItemViewModel : ObservableObject
{
    private readonly Action<string, bool> _onToggled;
    public IndependentRaceCatalogItem Race { get; }

    [ObservableProperty]
    private bool _isSelected;

    public IndependentAgendaRaceItemViewModel(IndependentRaceCatalogItem race, bool isSelected, Action<string, bool> onToggled)
    {
        Race = race;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        _onToggled(Race.Key, value);
    }
}

/// <summary>
/// Typed controls for the settings used by the WPF task modules, backed by the
/// same JSON payload so unknown/newer fields survive edits and round trips.
/// </summary>
public sealed partial class TaskSettingsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TaskCatalogOption[] AllTraineeOptions = TaskCatalogData.LoadTrainees();
    private static readonly TaskCatalogOption[] AllSupportCardOptions = TaskCatalogData.LoadSupportCards();
    private static readonly TaskCatalogOption[] AllSkillOptions = TaskCatalogData.LoadSkills();
    private static readonly TaskCatalogOption[] AllIndependentRaceOptions = TaskCatalogData.LoadIndependentRaces();
    private readonly TaskQueueItemViewModel _task;
    private string _rawSettingsJson;
    private string _jsonErrorText = string.Empty;
    private bool _isUpdating;

    private bool IsEnglish => _task.LocalizationService?.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<string> CareerModes { get; } = ["normal", "independent"];
    public IReadOnlyList<TaskSettingOption> CareerModeOptions => IsEnglish
        ? [new("independent", "Independent Agenda"), new("normal", "Normal Training")]
        : [new("independent", "独立日程育成"), new("normal", "经典属性育成")];

    public IReadOnlyList<string> DailyRaceModes { get; } = ["monies", "supportpoint"];
    public IReadOnlyList<TaskSettingOption> DailyRaceModeOptions => IsEnglish
        ? [new("monies", "Jupiter Cup (Monies)"), new("supportpoint", "Moonlight Prize (SP)")]
        : [new("monies", "木星杯·金币赛"), new("supportpoint", "月光赏·支援点数赛")];

    public IReadOnlyList<string> DailyRaceDifficulties { get; } = ["veryhard", "hard", "normal", "easy"];
    public IReadOnlyList<TaskSettingOption> DailyRaceDifficultyOptions => IsEnglish
        ? [new("veryhard", "Very Hard"), new("hard", "Hard"), new("normal", "Normal"), new("easy", "Easy")]
        : [new("veryhard", "极难 (Very Hard)"), new("hard", "困难 (Hard)"), new("normal", "普通 (Normal)"), new("easy", "简单 (Easy)")];

    public IReadOnlyList<string> SupportDeckModes { get; } = ["auto", "highest-star", "selected"];
    public IReadOnlyList<TaskSettingOption> SupportDeckModeOptions => IsEnglish
        ? [new("auto", "Auto Recommended"), new("highest-star", "Highest Star"), new("selected", "Manual Selected")]
        : [new("auto", "自动推荐配置"), new("highest-star", "最高星级优先"), new("selected", "手动精选卡组")];

    public IReadOnlyList<TaskSettingOption> SupportDeckPresets => IsEnglish
        ? [
            new("custom", "Custom"),
            new("speed3-stamina3", "3 Speed / 3 Stamina"),
            new("speed3-stamina2-wit1", "3 Speed / 2 Stamina / 1 Wit"),
            new("speed2-stamina2-power1-wit1", "2 Spd / 2 Sta / 1 Pow / 1 Wit"),
            new("speed2-stamina1-power1-wit1-friend1", "2 Spd / 1 Sta / 1 Pow / 1 Wit / 1 Friend"),
          ]
        : [
            new("custom", "自定义"),
            new("speed3-stamina3", "3速 3耐"),
            new("speed3-stamina2-wit1", "3速 2耐 1智"),
            new("speed2-stamina2-power1-wit1", "2速 2耐 1力 1智"),
            new("speed2-stamina1-power1-wit1-friend1", "2速 1耐 1力 1智 1友人"),
          ];

    public IReadOnlyList<string> LegacySelectionModes { get; } = ["auto", "manual"];
    public IReadOnlyList<TaskSettingOption> LegacySelectionModeOptions => IsEnglish
        ? [new("auto", "Auto"), new("manual", "Manual Sparks")]
        : [new("auto", "自动推荐最佳属性"), new("manual", "手动配置因子目标")];

    public IReadOnlyList<string> TrainingFocuses { get; } = ["balanced", "stamina", "sprint"];
    public IReadOnlyList<TaskSettingOption> TrainingFocusOptions => IsEnglish
        ? [new("balanced", "Balanced"), new("stamina", "Stamina Focus"), new("sprint", "Sprint Focus")]
        : [new("balanced", "均衡集训"), new("stamina", "长距离耐力专注"), new("sprint", "短距离速度爆发")];

    public IReadOnlyList<string> LineupStrategies { get; } = ["front", "pace", "late", "end"];
    public IReadOnlyList<TaskSettingOption> LineupStrategyOptions => IsEnglish
        ? [new("front", "Runner (Front)"), new("pace", "Leader (Pace)"), new("late", "Betweener (Late)"), new("end", "Chaser (End)")]
        : [new("front", "逃马"), new("pace", "先行"), new("late", "差行"), new("end", "追马")];

    public IReadOnlyList<TaskSettingOption> NormalTrainingStrategyOptions => IsEnglish
        ? [new("smart-balanced-safe", "Smart Balanced (Recommended)"), new("custom-ratio", "Custom Attribute Ratios")]
        : [new("smart-balanced-safe", "智能均衡策略（推荐）"), new("custom-ratio", "自定义五维训练比例")];

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(CareerModeOptions));
        OnPropertyChanged(nameof(DailyRaceModeOptions));
        OnPropertyChanged(nameof(DailyRaceDifficultyOptions));
        OnPropertyChanged(nameof(SupportDeckModeOptions));
        OnPropertyChanged(nameof(SupportDeckPresets));
        OnPropertyChanged(nameof(LegacySelectionModeOptions));
        OnPropertyChanged(nameof(TrainingFocusOptions));
        OnPropertyChanged(nameof(LineupStrategyOptions));
        OnPropertyChanged(nameof(NormalTrainingStrategyOptions));
        OnPropertyChanged(nameof(SelectedCareerModeOption));
        OnPropertyChanged(nameof(SelectedDailyRaceModeOption));
        OnPropertyChanged(nameof(SelectedDailyRaceDifficultyOption));
        OnPropertyChanged(nameof(SelectedSupportDeckModeOption));
        OnPropertyChanged(nameof(SelectedSupportDeckPreset));
        OnPropertyChanged(nameof(SelectedLegacySelectionModeOption));
        OnPropertyChanged(nameof(SelectedIndependentTrainingFocusOption));
        OnPropertyChanged(nameof(SelectedIndependentLineupStrategyOption));
        OnPropertyChanged(nameof(SelectedNormalLineupStrategyOption));
        OnPropertyChanged(nameof(SelectedNormalTrainingStrategyOption));
    }

    public IReadOnlyList<string> EventHandlingModes { get; } = ["default"];

    public IReadOnlyList<TaskCatalogOption> TraineeOptions { get; } = AllTraineeOptions;
    public IReadOnlyList<TaskCatalogOption> SupportCardOptions { get; } = AllSupportCardOptions;
    public IReadOnlyList<TaskCatalogOption> SkillOptions { get; } = AllSkillOptions;
    public IReadOnlyList<TaskCatalogOption> IndependentRaceOptions { get; } = AllIndependentRaceOptions;

    public IReadOnlyList<string> SupportCardTypeFilters { get; } =
        ["全部", "Speed", "Stamina", "Power", "Guts", "Wit", "Friend", "Group"];

    public IReadOnlyList<string> AgendaYearFilters { get; } =
        ["全部", "First Year", "Classic Year", "Senior Year"];

    public IReadOnlyList<string> AgendaSurfaceFilters { get; } =
        ["全部", "Turf", "Dirt"];

    public IReadOnlyList<string> AgendaDistanceFilters { get; } =
        ["全部", "Sprint", "Mile", "Medium", "Long"];

    public IReadOnlyList<string> AgendaGradeFilters { get; } =
        ["全部", "G1", "G2", "G3", "OP", "Pre-OP"];

    private TaskCatalogOption[] _filteredTraineeOptions = [];
    private TaskCatalogOption[] _filteredSupportCardOptions = [];
    private TaskCatalogOption[] _filteredSkillOptions = [];
    private TaskCatalogOption[] _filteredIndependentRaceOptions = [];

    private string _traineeSearchText = string.Empty;
    private string _supportCardSearchText = string.Empty;
    private string _skillSearchText = string.Empty;
    private string _independentRaceSearchText = string.Empty;

    private string _friendCardSearchText = string.Empty;
    private string _friendCardTypeFilter = "全部";
    private string _manualSupportCardTypeFilter = "全部";

    private string _normalSkillSearchText = string.Empty;
    private string _independentSkillSearchText = string.Empty;

    private string _agendaYearFilter = "全部";
    private string _agendaSurfaceFilter = "全部";
    private string _agendaDistanceFilter = "全部";
    private string _agendaGradeFilter = "全部";
    private string _agendaSearchText = string.Empty;

    private List<TraineeCatalogItem> _richFilteredTrainees = [];
    private List<SupportCardCatalogItem> _richFilteredSupportCards = [];
    private List<SupportCardCatalogItem> _richFilteredFriendCards = [];
    private List<SkillCatalogItem> _richFilteredNormalSkills = [];
    private List<SkillCatalogItem> _richFilteredIndependentSkills = [];
    private List<IndependentAgendaRaceItemViewModel> _richFilteredAgendaRaces = [];

    public ObservableCollection<SupportCardCatalogItem> SelectedSupportCards { get; } = [];
    public ObservableCollection<SkillCatalogItem> TargetNormalSkills { get; } = [];
    public ObservableCollection<SkillCatalogItem> TargetIndependentSkills { get; } = [];

    [ObservableProperty]
    private SkillCatalogItem? _selectedAvailableNormalSkill;

    [ObservableProperty]
    private SkillCatalogItem? _selectedTargetNormalSkill;

    [ObservableProperty]
    private SkillCatalogItem? _selectedAvailableIndependentSkill;

    [ObservableProperty]
    private SkillCatalogItem? _selectedTargetIndependentSkill;

    public string TaskId => _task.TaskId;
    public bool IsStartGame => TaskId == "start-game";
    public bool IsCareerTraining => TaskId == "career-training";
    public bool IsDailyRace => TaskId == "daily-race";
    public bool IsTeamRace => TaskId == "team-race";
    public bool IsDefinitionTask => IsDailyRace || IsTeamRace || TaskId is "mail-collection" or "mission-collection";
    public bool IsSimpleCollectionTask => TaskId is "mail-collection" or "mission-collection";
    public bool IsNormalCareer => IsCareerTraining && CareerMode == "normal";
    public bool IsIndependentCareer => IsCareerTraining && CareerMode == "independent";

    public IReadOnlyList<TaskCatalogOption> FilteredTraineeOptions => _filteredTraineeOptions;
    public IReadOnlyList<TaskCatalogOption> FilteredSupportCardOptions => _filteredSupportCardOptions;
    public IReadOnlyList<TaskCatalogOption> FilteredSkillOptions => _filteredSkillOptions;
    public IReadOnlyList<TaskCatalogOption> FilteredIndependentRaceOptions => _filteredIndependentRaceOptions;

    public IReadOnlyList<TraineeCatalogItem> RichFilteredTrainees => _richFilteredTrainees;
    public IReadOnlyList<SupportCardCatalogItem> RichFilteredSupportCards => _richFilteredSupportCards;
    public IReadOnlyList<SupportCardCatalogItem> RichFilteredFriendCards => _richFilteredFriendCards;
    public IReadOnlyList<SkillCatalogItem> RichFilteredNormalSkills => _richFilteredNormalSkills;
    public IReadOnlyList<SkillCatalogItem> RichFilteredIndependentSkills => _richFilteredIndependentSkills;
    public IReadOnlyList<IndependentAgendaRaceItemViewModel> RichFilteredAgendaRaces => _richFilteredAgendaRaces;

    public string TraineeSearchText
    {
        get => _traineeSearchText;
        set
        {
            if (!SetProperty(ref _traineeSearchText, value ?? string.Empty)) return;
            _filteredTraineeOptions = Filter(TraineeOptions, _traineeSearchText);
            FilterRichTrainees();
            OnPropertyChanged(nameof(FilteredTraineeOptions));
            OnPropertyChanged(nameof(RichFilteredTrainees));
        }
    }

    public string SupportCardSearchText
    {
        get => _supportCardSearchText;
        set
        {
            if (!SetProperty(ref _supportCardSearchText, value ?? string.Empty)) return;
            _filteredSupportCardOptions = Filter(SupportCardOptions, _supportCardSearchText);
            FilterRichSupportCards();
            OnPropertyChanged(nameof(FilteredSupportCardOptions));
            OnPropertyChanged(nameof(RichFilteredSupportCards));
        }
    }

    public string ManualSupportCardTypeFilter
    {
        get => _manualSupportCardTypeFilter;
        set
        {
            if (!SetProperty(ref _manualSupportCardTypeFilter, value ?? "全部")) return;
            FilterRichSupportCards();
            OnPropertyChanged(nameof(RichFilteredSupportCards));
        }
    }

    public string FriendCardSearchText
    {
        get => _friendCardSearchText;
        set
        {
            if (!SetProperty(ref _friendCardSearchText, value ?? string.Empty)) return;
            FilterRichFriendCards();
            OnPropertyChanged(nameof(RichFilteredFriendCards));
        }
    }

    public string FriendCardTypeFilter
    {
        get => _friendCardTypeFilter;
        set
        {
            if (!SetProperty(ref _friendCardTypeFilter, value ?? "全部")) return;
            FilterRichFriendCards();
            OnPropertyChanged(nameof(RichFilteredFriendCards));
        }
    }

    public string NormalSkillSearchText
    {
        get => _normalSkillSearchText;
        set
        {
            if (!SetProperty(ref _normalSkillSearchText, value ?? string.Empty)) return;
            FilterRichNormalSkills();
            OnPropertyChanged(nameof(RichFilteredNormalSkills));
        }
    }

    public string IndependentSkillSearchText
    {
        get => _independentSkillSearchText;
        set
        {
            if (!SetProperty(ref _independentSkillSearchText, value ?? string.Empty)) return;
            FilterRichIndependentSkills();
            OnPropertyChanged(nameof(RichFilteredIndependentSkills));
        }
    }

    public string SkillSearchText
    {
        get => _skillSearchText;
        set
        {
            if (!SetProperty(ref _skillSearchText, value ?? string.Empty)) return;
            _filteredSkillOptions = Filter(SkillOptions, _skillSearchText);
            OnPropertyChanged(nameof(FilteredSkillOptions));
        }
    }

    public string IndependentRaceSearchText
    {
        get => _independentRaceSearchText;
        set
        {
            if (!SetProperty(ref _independentRaceSearchText, value ?? string.Empty)) return;
            _filteredIndependentRaceOptions = Filter(IndependentRaceOptions, _independentRaceSearchText);
            OnPropertyChanged(nameof(FilteredIndependentRaceOptions));
        }
    }

    public string AgendaSearchText
    {
        get => _agendaSearchText;
        set
        {
            if (!SetProperty(ref _agendaSearchText, value ?? string.Empty)) return;
            FilterRichAgendaRaces();
            OnPropertyChanged(nameof(RichFilteredAgendaRaces));
        }
    }

    public string AgendaYearFilter
    {
        get => _agendaYearFilter;
        set
        {
            if (!SetProperty(ref _agendaYearFilter, value ?? "全部")) return;
            FilterRichAgendaRaces();
            OnPropertyChanged(nameof(RichFilteredAgendaRaces));
        }
    }

    public string AgendaSurfaceFilter
    {
        get => _agendaSurfaceFilter;
        set
        {
            if (!SetProperty(ref _agendaSurfaceFilter, value ?? "全部")) return;
            FilterRichAgendaRaces();
            OnPropertyChanged(nameof(RichFilteredAgendaRaces));
        }
    }

    public string AgendaDistanceFilter
    {
        get => _agendaDistanceFilter;
        set
        {
            if (!SetProperty(ref _agendaDistanceFilter, value ?? "全部")) return;
            FilterRichAgendaRaces();
            OnPropertyChanged(nameof(RichFilteredAgendaRaces));
        }
    }

    public string AgendaGradeFilter
    {
        get => _agendaGradeFilter;
        set
        {
            if (!SetProperty(ref _agendaGradeFilter, value ?? "全部")) return;
            FilterRichAgendaRaces();
            OnPropertyChanged(nameof(RichFilteredAgendaRaces));
        }
    }

    public string RawSettingsJson
    {
        get => _rawSettingsJson;
        set
        {
            if (!SetProperty(ref _rawSettingsJson, value ?? string.Empty) || _isUpdating)
                return;

            try
            {
                if (JsonNode.Parse(_rawSettingsJson) is not JsonObject parsed)
                    throw new JsonException("The task settings must be a JSON object.");
                _jsonErrorText = string.Empty;
                OnPropertyChanged(nameof(JsonErrorText));
                _task.ReplaceSettings(parsed);
                NotifyAllTypedProperties();
                NotifyValidationChanged();
            }
            catch (JsonException exception)
            {
                _jsonErrorText = exception.Message;
                OnPropertyChanged(nameof(JsonErrorText));
                NotifyValidationChanged();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string JsonErrorText => _jsonErrorText;
    public bool HasJsonError => !string.IsNullOrEmpty(_jsonErrorText);
    public bool IsValid => string.IsNullOrEmpty(ValidationMessage);
    public string ValidationMessage => HasJsonError ? $"JSON 格式错误：{JsonErrorText}" : GetValidationMessage();
    public event EventHandler? SettingsChanged;

    public string PackageId
    {
        get => ReadString("packageId", "com.cygames.umamusume");
        set => SetString("packageId", value);
    }

    public string ActivityName
    {
        get => ReadString("activityName", string.Empty);
        set => SetString("activityName", value);
    }

    public string DefinitionPath
    {
        get => ReadString("definitionPath", string.Empty);
        set => SetString("definitionPath", value);
    }

    public string RaceCountText
    {
        get => ReadInt("raceCount", IsDailyRace ? 1 : 3).ToString(CultureInfo.InvariantCulture);
        set => SetInt("raceCount", value, IsDailyRace ? 1 : 3, IsDailyRace ? 6 : 5);
    }

    public bool StopWhenTicketsEmpty
    {
        get => ReadBool("stopWhenTicketsEmpty", true);
        set => SetBool("stopWhenTicketsEmpty", value);
    }

    public string DailyRaceMode
    {
        get => ReadString("mode", "monies");
        set
        {
            SetString("mode", value);
            OnPropertyChanged(nameof(SelectedDailyRaceModeOption));
        }
    }

    public TaskSettingOption? SelectedDailyRaceModeOption
    {
        get => DailyRaceModeOptions.FirstOrDefault(opt => opt.Value.Equals(DailyRaceMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                DailyRaceMode = value.Value;
        }
    }

    public string DailyRaceDifficulty
    {
        get => ReadString("difficulty", "veryhard");
        set
        {
            SetString("difficulty", value);
            OnPropertyChanged(nameof(SelectedDailyRaceDifficultyOption));
        }
    }

    public TaskSettingOption? SelectedDailyRaceDifficultyOption
    {
        get => DailyRaceDifficultyOptions.FirstOrDefault(opt => opt.Value.Equals(DailyRaceDifficulty, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                DailyRaceDifficulty = value.Value;
        }
    }

    public string TraineeIdText
    {
        get => ReadNullableInt("traineeId");
        set
        {
            SetNullableInt("traineeId", value);
            OnPropertyChanged(nameof(SelectedTraineeItem));
            OnPropertyChanged(nameof(SelectedTraineeOption));
        }
    }

    public TraineeCatalogItem? SelectedTraineeItem
    {
        get => UmaCatalogService.FindTrainee(TraineeIdText);
        set
        {
            TraineeIdText = value?.TraineeId.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(SelectedTraineeItem));
            OnPropertyChanged(nameof(SelectedTraineeOption));
        }
    }

    public TaskCatalogOption? SelectedTraineeOption
    {
        get => TraineeOptions.FirstOrDefault(option => option.Id == TraineeIdText);
        set
        {
            if (value is not null)
                TraineeIdText = value.Id;
        }
    }

    public TaskSettingOption? SelectedSupportDeckPreset
    {
        get => SupportDeckPresets.FirstOrDefault(option => option.Value.Equals(SupportDeckPreset, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                SupportDeckPreset = value.Value;
        }
    }

    public TaskCatalogOption? SelectedSupportCardOption
    {
        get => null;
        set
        {
            if (value is null)
                return;
            SupportDeckMode = "selected";
            SupportCardIdsText = AppendId(SupportCardIdsText, value.Id);
        }
    }

    public TaskCatalogOption? SelectedFriendSupportCardOption
    {
        get => SupportCardOptions.FirstOrDefault(option => option.Id == FriendSupportCardIdText);
        set
        {
            if (value is not null)
                FriendSupportCardIdText = value.Id;
        }
    }

    public SupportCardCatalogItem? SelectedFriendCard
    {
        get => UmaCatalogService.FindSupportCard(FriendSupportCardIdText);
        set
        {
            FriendSupportCardIdText = value?.SupportCardId.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            OnPropertyChanged(nameof(SelectedFriendCard));
            OnPropertyChanged(nameof(SelectedFriendSupportCardOption));
        }
    }

    public string ManifestPath
    {
        get => ReadString("manifestPath", "resource/hachimi/ura/manifest.json");
        set => SetString("manifestPath", value);
    }

    public string CareerMode
    {
        get => ReadString("careerMode", "independent");
        set
        {
            SetString("careerMode", value);
            OnPropertyChanged(nameof(IsNormalCareer));
            OnPropertyChanged(nameof(IsIndependentCareer));
            OnPropertyChanged(nameof(SelectedCareerModeOption));
        }
    }

    public TaskSettingOption? SelectedCareerModeOption
    {
        get => CareerModeOptions.FirstOrDefault(opt => opt.Value.Equals(CareerMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                CareerMode = value.Value;
        }
    }

    public string RunCountText
    {
        get => ReadInt("runCount", 1).ToString(CultureInfo.InvariantCulture);
        set => SetInt("runCount", value, 1, int.MaxValue);
    }

    public string StrategyId
    {
        get => ReadString("strategyId", "default-speed-medium");
        set => SetString("strategyId", value);
    }

    public string NormalLineupStrategy
    {
        get => ReadString("normalLineupStrategy", "front");
        set
        {
            SetString("normalLineupStrategy", value);
            OnPropertyChanged(nameof(SelectedNormalLineupStrategyOption));
        }
    }

    public TaskSettingOption? SelectedNormalLineupStrategyOption
    {
        get => LineupStrategyOptions.FirstOrDefault(opt => opt.Value.Equals(NormalLineupStrategy, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                NormalLineupStrategy = value.Value;
        }
    }

    public string NormalTrainingStrategy
    {
        get => ReadString("normalTrainingStrategy", "smart-balanced-safe");
        set
        {
            SetString("normalTrainingStrategy", value);
            OnPropertyChanged(nameof(IsCustomNormalTrainingStrategy));
            OnPropertyChanged(nameof(SelectedNormalTrainingStrategyOption));
        }
    }

    public TaskSettingOption? SelectedNormalTrainingStrategyOption
    {
        get => NormalTrainingStrategyOptions.FirstOrDefault(opt => opt.Value.Equals(NormalTrainingStrategy, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                NormalTrainingStrategy = value.Value;
        }
    }

    public bool IsCustomNormalTrainingStrategy => NormalTrainingStrategy == "custom-ratio";

    public string NormalTrainingSpeedText
    {
        get => ReadRatio("speed", 2).ToString(CultureInfo.InvariantCulture);
        set { SetRatio("speed", value); NotifyRatioUpdated(); }
    }

    public string NormalTrainingStaminaText
    {
        get => ReadRatio("stamina", 1).ToString(CultureInfo.InvariantCulture);
        set { SetRatio("stamina", value); NotifyRatioUpdated(); }
    }

    public string NormalTrainingPowerText
    {
        get => ReadRatio("power", 0).ToString(CultureInfo.InvariantCulture);
        set { SetRatio("power", value); NotifyRatioUpdated(); }
    }

    public string NormalTrainingGutsText
    {
        get => ReadRatio("guts", 0).ToString(CultureInfo.InvariantCulture);
        set { SetRatio("guts", value); NotifyRatioUpdated(); }
    }

    public string NormalTrainingWitText
    {
        get => ReadRatio("wit", 0).ToString(CultureInfo.InvariantCulture);
        set { SetRatio("wit", value); NotifyRatioUpdated(); }
    }

    public int SpeedRatio => int.TryParse(NormalTrainingSpeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int StaminaRatio => int.TryParse(NormalTrainingStaminaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int PowerRatio => int.TryParse(NormalTrainingPowerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int GutsRatio => int.TryParse(NormalTrainingGutsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int WitRatio => int.TryParse(NormalTrainingWitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public int TotalRatio => SpeedRatio + StaminaRatio + PowerRatio + GutsRatio + WitRatio;
    public bool IsRatioInvalid => TotalRatio == 0;

    public string SpeedPercentage => TotalRatio > 0 ? $"{Math.Round((double)SpeedRatio / TotalRatio * 100, 1):0.#}%" : "0%";
    public string StaminaPercentage => TotalRatio > 0 ? $"{Math.Round((double)StaminaRatio / TotalRatio * 100, 1):0.#}%" : "0%";
    public string PowerPercentage => TotalRatio > 0 ? $"{Math.Round((double)PowerRatio / TotalRatio * 100, 1):0.#}%" : "0%";
    public string GutsPercentage => TotalRatio > 0 ? $"{Math.Round((double)GutsRatio / TotalRatio * 100, 1):0.#}%" : "0%";
    public string WitPercentage => TotalRatio > 0 ? $"{Math.Round((double)WitRatio / TotalRatio * 100, 1):0.#}%" : "0%";

    public string RatioCyclePreview
    {
        get
        {
            if (IsRatioInvalid) return "—";
            var weights = new[] { SpeedRatio, StaminaRatio, PowerRatio, GutsRatio, WitRatio };
            var divisor = weights.Aggregate(0, GreatestCommonDivisor);
            if (divisor <= 0) return "—";
            string[] labels = ["速度", "耐力", "力量", "根性", "智力"];
            var preview = new List<string>();
            for (var i = 0; i < weights.Length; i++)
            {
                var count = weights[i] / divisor;
                if (count > 0)
                    preview.Add($"{labels[i]} ({count})");
            }
            return string.Join(" · ", preview);
        }
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var temp = a % b;
            a = b;
            b = temp;
        }
        return a;
    }

    private void NotifyRatioUpdated()
    {
        OnPropertyChanged(nameof(SpeedRatio));
        OnPropertyChanged(nameof(StaminaRatio));
        OnPropertyChanged(nameof(PowerRatio));
        OnPropertyChanged(nameof(GutsRatio));
        OnPropertyChanged(nameof(WitRatio));
        OnPropertyChanged(nameof(TotalRatio));
        OnPropertyChanged(nameof(IsRatioInvalid));
        OnPropertyChanged(nameof(SpeedPercentage));
        OnPropertyChanged(nameof(StaminaPercentage));
        OnPropertyChanged(nameof(PowerPercentage));
        OnPropertyChanged(nameof(GutsPercentage));
        OnPropertyChanged(nameof(WitPercentage));
        OnPropertyChanged(nameof(RatioCyclePreview));
    }

    public bool RetryFailedRaceWithAlarmClock
    {
        get => ReadBool("retryFailedRaceWithAlarmClock", false);
        set => SetBool("retryFailedRaceWithAlarmClock", value);
    }

    public string NormalEventHandling
    {
        get => ReadString("normalEventHandling", "default");
        set => SetString("normalEventHandling", value);
    }

    public string NormalSkillIdsText
    {
        get => ReadIntArray("normalSkillIds");
        set
        {
            SetIntArray("normalSkillIds", value);
            SyncTargetNormalSkills();
        }
    }

    public TaskCatalogOption? SelectedNormalSkillOption
    {
        get => null;
        set
        {
            if (value is not null)
                NormalSkillIdsText = AppendId(NormalSkillIdsText, value.Id);
        }
    }

    public string IndependentTrainingFocus
    {
        get => ReadString("independentTrainingFocus", "balanced");
        set
        {
            SetString("independentTrainingFocus", value);
            OnPropertyChanged(nameof(SelectedIndependentTrainingFocusOption));
        }
    }

    public TaskSettingOption? SelectedIndependentTrainingFocusOption
    {
        get => TrainingFocusOptions.FirstOrDefault(opt => opt.Value.Equals(IndependentTrainingFocus, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                IndependentTrainingFocus = value.Value;
        }
    }

    public string IndependentLineupStrategy
    {
        get => ReadString("independentLineupStrategy", "pace");
        set
        {
            SetString("independentLineupStrategy", value);
            OnPropertyChanged(nameof(SelectedIndependentLineupStrategyOption));
        }
    }

    public TaskSettingOption? SelectedIndependentLineupStrategyOption
    {
        get => LineupStrategyOptions.FirstOrDefault(opt => opt.Value.Equals(IndependentLineupStrategy, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                IndependentLineupStrategy = value.Value;
        }
    }

    public string IndependentAgendaSelectionsText
    {
        get => ReadStringArray("independentAgendaSelections");
        set
        {
            SetStringArray("independentAgendaSelections", value);
            OnPropertyChanged(nameof(SelectedAgendaRaceCountText));
            RefreshAgendaRaceSelection();
        }
    }

    public string SelectedAgendaRaceCountText =>
        $"已安排日程赛事：{ParseParts(IndependentAgendaSelectionsText).Length} 场";

    public string IndependentSkillIdsText
    {
        get => ReadIntArray("independentSkillIds");
        set
        {
            SetIntArray("independentSkillIds", value);
            SyncTargetIndependentSkills();
        }
    }

    public TaskCatalogOption? SelectedIndependentSkillOption
    {
        get => null;
        set
        {
            if (value is not null)
                IndependentSkillIdsText = AppendId(IndependentSkillIdsText, value.Id);
        }
    }

    public TaskCatalogOption? SelectedIndependentAgendaRaceOption
    {
        get => null;
        set
        {
            if (value is null)
                return;
            var current = ParseParts(IndependentAgendaSelectionsText).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.Add(value.Id))
                IndependentAgendaSelectionsText = string.Join(Environment.NewLine, current);
        }
    }

    public bool DeleteExistingCareerData
    {
        get => ReadBool("deleteExistingCareerData", false);
        set
        {
            SetBool("deleteExistingCareerData", value);
            SetBool("continueExistingCareer", !value);
        }
    }

    public bool AutoRestoreTp
    {
        get => ReadBool("autoRestoreTp", false);
        set => SetBool("autoRestoreTp", value);
    }

    public string SupportDeckMode
    {
        get => ReadString("supportDeckMode", "auto");
        set
        {
            SetString("supportDeckMode", value);
            OnPropertyChanged(nameof(IsManualSupportDeck));
            OnPropertyChanged(nameof(IsSupportPresetMode));
            OnPropertyChanged(nameof(SelectedSupportDeckModeOption));
        }
    }

    public TaskSettingOption? SelectedSupportDeckModeOption
    {
        get => SupportDeckModeOptions.FirstOrDefault(opt => opt.Value.Equals(SupportDeckMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                SupportDeckMode = value.Value;
        }
    }

    public bool IsManualSupportDeck => SupportDeckMode == "selected";
    public bool IsSupportPresetMode => SupportDeckMode != "selected";

    public string SupportDeckPreset
    {
        get => ReadString("supportDeckPreset", "custom");
        set
        {
            SetString("supportDeckPreset", value);
            OnPropertyChanged(nameof(SelectedSupportDeckPreset));
        }
    }

    public string FriendSupportCardIdText
    {
        get => ReadNullableInt("friendSupportCardId");
        set
        {
            SetNullableInt("friendSupportCardId", value);
            OnPropertyChanged(nameof(SelectedFriendCard));
            OnPropertyChanged(nameof(SelectedFriendSupportCardOption));
        }
    }

    public string SupportCardIdsText
    {
        get => ReadIntArray("supportCardIds");
        set
        {
            SetIntArray("supportCardIds", value);
            SyncSelectedSupportCards();
        }
    }

    public string SelectedSupportCardCountText => $"已选支援卡：{SelectedSupportCards.Count} / 6";

    public bool PauseOnUnknownOutcome
    {
        get => ReadBool("pauseOnUnknownOutcome", true);
        set => SetBool("pauseOnUnknownOutcome", value);
    }

    public bool AllowOptionalRaces
    {
        get => ReadBool("allowOptionalRaces", false);
        set => SetBool("allowOptionalRaces", value);
    }

    public string LegacySelectionMode
    {
        get => ReadString("legacySelectionMode", "auto");
        set
        {
            SetString("legacySelectionMode", value);
            OnPropertyChanged(nameof(IsManualLegacySelection));
            OnPropertyChanged(nameof(SelectedLegacySelectionModeOption));
        }
    }

    public TaskSettingOption? SelectedLegacySelectionModeOption
    {
        get => LegacySelectionModeOptions.FirstOrDefault(opt => opt.Value.Equals(LegacySelectionMode, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
                LegacySelectionMode = value.Value;
        }
    }

    public bool IsManualLegacySelection => LegacySelectionMode == "manual";

    public bool UseLegacyGuest
    {
        get => ReadBool("useLegacyGuest", false);
        set => SetBool("useLegacyGuest", value);
    }

    public bool UseCachedLegacy
    {
        get => ReadBool("useCachedLegacy", true);
        set => SetBool("useCachedLegacy", value);
    }

    public string LegacyAttributeSparksText
    {
        get => ReadStringArray("legacyAttributeSparks");
        set
        {
            SetStringArray("legacyAttributeSparks", value);
            NotifyAttributeSparksChanged();
        }
    }

    public string LegacyAptitudeSparksText
    {
        get => ReadStringArray("legacyAptitudeSparks");
        set
        {
            SetStringArray("legacyAptitudeSparks", value);
            NotifyAptitudeSparksChanged();
        }
    }

    // Legacy Attribute Sparks (5)
    public bool SparkSpeed { get => HasSpark("legacyAttributeSparks", "speed"); set => ToggleSpark("legacyAttributeSparks", "speed", value); }
    public bool SparkStamina { get => HasSpark("legacyAttributeSparks", "stamina"); set => ToggleSpark("legacyAttributeSparks", "stamina", value); }
    public bool SparkPower { get => HasSpark("legacyAttributeSparks", "power"); set => ToggleSpark("legacyAttributeSparks", "power", value); }
    public bool SparkGuts { get => HasSpark("legacyAttributeSparks", "guts"); set => ToggleSpark("legacyAttributeSparks", "guts", value); }
    public bool SparkWit { get => HasSpark("legacyAttributeSparks", "wit"); set => ToggleSpark("legacyAttributeSparks", "wit", value); }

    // Legacy Aptitude Sparks (10)
    public bool SparkTurf { get => HasSpark("legacyAptitudeSparks", "turf"); set => ToggleSpark("legacyAptitudeSparks", "turf", value); }
    public bool SparkDirt { get => HasSpark("legacyAptitudeSparks", "dirt"); set => ToggleSpark("legacyAptitudeSparks", "dirt", value); }
    public bool SparkShort { get => HasSpark("legacyAptitudeSparks", "short"); set => ToggleSpark("legacyAptitudeSparks", "short", value); }
    public bool SparkMile { get => HasSpark("legacyAptitudeSparks", "mile"); set => ToggleSpark("legacyAptitudeSparks", "mile", value); }
    public bool SparkMedium { get => HasSpark("legacyAptitudeSparks", "medium"); set => ToggleSpark("legacyAptitudeSparks", "medium", value); }
    public bool SparkLong { get => HasSpark("legacyAptitudeSparks", "long"); set => ToggleSpark("legacyAptitudeSparks", "long", value); }
    public bool SparkRunner { get => HasSpark("legacyAptitudeSparks", "runner"); set => ToggleSpark("legacyAptitudeSparks", "runner", value); }
    public bool SparkLeader { get => HasSpark("legacyAptitudeSparks", "leader"); set => ToggleSpark("legacyAptitudeSparks", "leader", value); }
    public bool SparkBetweener { get => HasSpark("legacyAptitudeSparks", "betweener"); set => ToggleSpark("legacyAptitudeSparks", "betweener", value); }
    public bool SparkChaser { get => HasSpark("legacyAptitudeSparks", "chaser"); set => ToggleSpark("legacyAptitudeSparks", "chaser", value); }

    private bool HasSpark(string key, string spark)
    {
        if (_task.Settings[key] is not JsonArray array) return false;
        return array.Any(item => string.Equals(item?.GetValue<string>(), spark, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleSpark(string key, string spark, bool enabled)
    {
        var raw = key == "legacyAttributeSparks" ? LegacyAttributeSparksText : LegacyAptitudeSparksText;
        var current = ParseParts(raw).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled) current.Add(spark);
        else current.Remove(spark);

        if (key == "legacyAttributeSparks")
        {
            LegacyAttributeSparksText = string.Join(Environment.NewLine, current);
        }
        else
        {
            LegacyAptitudeSparksText = string.Join(Environment.NewLine, current);
        }
    }

    private void NotifyAttributeSparksChanged()
    {
        OnPropertyChanged(nameof(SparkSpeed));
        OnPropertyChanged(nameof(SparkStamina));
        OnPropertyChanged(nameof(SparkPower));
        OnPropertyChanged(nameof(SparkGuts));
        OnPropertyChanged(nameof(SparkWit));
    }

    private void NotifyAptitudeSparksChanged()
    {
        OnPropertyChanged(nameof(SparkTurf));
        OnPropertyChanged(nameof(SparkDirt));
        OnPropertyChanged(nameof(SparkShort));
        OnPropertyChanged(nameof(SparkMile));
        OnPropertyChanged(nameof(SparkMedium));
        OnPropertyChanged(nameof(SparkLong));
        OnPropertyChanged(nameof(SparkRunner));
        OnPropertyChanged(nameof(SparkLeader));
        OnPropertyChanged(nameof(SparkBetweener));
        OnPropertyChanged(nameof(SparkChaser));
    }

    public TaskSettingsViewModel(TaskQueueItemViewModel task)
    {
        _task = task;
        _rawSettingsJson = task.Settings.ToJsonString(JsonOptions);
        _filteredTraineeOptions = AllTraineeOptions;
        _filteredSupportCardOptions = AllSupportCardOptions;
        _filteredSkillOptions = AllSkillOptions;
        _filteredIndependentRaceOptions = AllIndependentRaceOptions;

        FilterRichTrainees();
        FilterRichSupportCards();
        FilterRichFriendCards();
        FilterRichNormalSkills();
        FilterRichIndependentSkills();
        FilterRichAgendaRaces();
        SyncSelectedSupportCards();
        SyncTargetNormalSkills();
        SyncTargetIndependentSkills();
    }

    public void Refresh()
    {
        _isUpdating = true;
        _rawSettingsJson = _task.Settings.ToJsonString(JsonOptions);
        OnPropertyChanged(nameof(RawSettingsJson));
        NotifyAllTypedProperties();
        SyncSelectedSupportCards();
        SyncTargetNormalSkills();
        SyncTargetIndependentSkills();
        RefreshAgendaRaceSelection();
        _isUpdating = false;
    }

    // Support Deck commands
    [RelayCommand]
    public void AddSupportCard(SupportCardCatalogItem card)
    {
        var currentIds = ParseParts(SupportCardIdsText).ToList();
        if (!currentIds.Contains(card.SupportCardId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
        {
            currentIds.Add(card.SupportCardId.ToString(CultureInfo.InvariantCulture));
            SupportCardIdsText = string.Join(", ", currentIds);
        }
    }

    [RelayCommand]
    public void RemoveSupportCard(SupportCardCatalogItem card)
    {
        var currentIds = ParseParts(SupportCardIdsText).ToList();
        currentIds.RemoveAll(id => id.Equals(card.SupportCardId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
        SupportCardIdsText = string.Join(", ", currentIds);
    }

    [RelayCommand]
    public void ClearFriendCard()
    {
        SelectedFriendCard = null;
    }

    [RelayCommand]
    public void ClearTrainee()
    {
        SelectedTraineeItem = null;
    }

    private void SyncSelectedSupportCards()
    {
        SelectedSupportCards.Clear();
        var currentIds = ParseParts(SupportCardIdsText);
        foreach (var id in currentIds)
        {
            var card = UmaCatalogService.FindSupportCard(id) ??
                (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
                    ? new SupportCardCatalogItem(parsedId, $"支援卡 #{parsedId}", "未知", "SSR", string.Empty)
                    : null);
            if (card is not null)
                SelectedSupportCards.Add(card);
        }
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
    }

    // Normal Skills commands
    [RelayCommand]
    public void AddSelectedNormalSkill()
    {
        if (SelectedAvailableNormalSkill is not { } skill) return;
        var current = ParseParts(NormalSkillIdsText).ToList();
        if (!current.Contains(skill.SkillId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
        {
            current.Add(skill.SkillId.ToString(CultureInfo.InvariantCulture));
            NormalSkillIdsText = string.Join(", ", current);
        }
    }

    [RelayCommand]
    public void RemoveSelectedNormalSkill()
    {
        if (SelectedTargetNormalSkill is not { } skill) return;
        var current = ParseParts(NormalSkillIdsText).ToList();
        current.RemoveAll(id => id.Equals(skill.SkillId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
        NormalSkillIdsText = string.Join(", ", current);
    }

    [RelayCommand]
    public void MoveNormalSkillUp()
    {
        if (SelectedTargetNormalSkill is not { } skill) return;
        var idx = TargetNormalSkills.IndexOf(skill);
        if (idx > 0)
        {
            var current = ParseParts(NormalSkillIdsText).ToList();
            var item = current[idx];
            current.RemoveAt(idx);
            current.Insert(idx - 1, item);
            NormalSkillIdsText = string.Join(", ", current);
            SelectedTargetNormalSkill = skill;
        }
    }

    [RelayCommand]
    public void MoveNormalSkillDown()
    {
        if (SelectedTargetNormalSkill is not { } skill) return;
        var idx = TargetNormalSkills.IndexOf(skill);
        if (idx >= 0 && idx < TargetNormalSkills.Count - 1)
        {
            var current = ParseParts(NormalSkillIdsText).ToList();
            var item = current[idx];
            current.RemoveAt(idx);
            current.Insert(idx + 1, item);
            NormalSkillIdsText = string.Join(", ", current);
            SelectedTargetNormalSkill = skill;
        }
    }

    private void SyncTargetNormalSkills()
    {
        TargetNormalSkills.Clear();
        var currentIds = ParseParts(NormalSkillIdsText);
        foreach (var id in currentIds)
        {
            var skill = UmaCatalogService.FindSkill(id) ??
                (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
                    ? new SkillCatalogItem(parsedId, $"技能 #{parsedId}", "常规", null, string.Empty)
                    : null);
            if (skill is not null)
                TargetNormalSkills.Add(skill);
        }
        OnPropertyChanged(nameof(TargetNormalSkillCountText));
    }

    public string TargetNormalSkillCountText => $"已选目标技能：{TargetNormalSkills.Count}";

    // Independent Skills commands
    [RelayCommand]
    public void AddSelectedIndependentSkill()
    {
        if (SelectedAvailableIndependentSkill is not { } skill) return;
        var current = ParseParts(IndependentSkillIdsText).ToList();
        if (!current.Contains(skill.SkillId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
        {
            current.Add(skill.SkillId.ToString(CultureInfo.InvariantCulture));
            IndependentSkillIdsText = string.Join(", ", current);
        }
    }

    [RelayCommand]
    public void RemoveSelectedIndependentSkill()
    {
        if (SelectedTargetIndependentSkill is not { } skill) return;
        var current = ParseParts(IndependentSkillIdsText).ToList();
        current.RemoveAll(id => id.Equals(skill.SkillId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
        IndependentSkillIdsText = string.Join(", ", current);
    }

    [RelayCommand]
    public void MoveIndependentSkillUp()
    {
        if (SelectedTargetIndependentSkill is not { } skill) return;
        var idx = TargetIndependentSkills.IndexOf(skill);
        if (idx > 0)
        {
            var current = ParseParts(IndependentSkillIdsText).ToList();
            var item = current[idx];
            current.RemoveAt(idx);
            current.Insert(idx - 1, item);
            IndependentSkillIdsText = string.Join(", ", current);
            SelectedTargetIndependentSkill = skill;
        }
    }

    [RelayCommand]
    public void MoveIndependentSkillDown()
    {
        if (SelectedTargetIndependentSkill is not { } skill) return;
        var idx = TargetIndependentSkills.IndexOf(skill);
        if (idx >= 0 && idx < TargetIndependentSkills.Count - 1)
        {
            var current = ParseParts(IndependentSkillIdsText).ToList();
            var item = current[idx];
            current.RemoveAt(idx);
            current.Insert(idx + 1, item);
            IndependentSkillIdsText = string.Join(", ", current);
            SelectedTargetIndependentSkill = skill;
        }
    }

    private void SyncTargetIndependentSkills()
    {
        TargetIndependentSkills.Clear();
        var currentIds = ParseParts(IndependentSkillIdsText);
        foreach (var id in currentIds)
        {
            var skill = UmaCatalogService.FindSkill(id) ??
                (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
                    ? new SkillCatalogItem(parsedId, $"技能 #{parsedId}", "独立", null, string.Empty)
                    : null);
            if (skill is not null)
                TargetIndependentSkills.Add(skill);
        }
        OnPropertyChanged(nameof(TargetIndependentSkillCountText));
    }

    public string TargetIndependentSkillCountText => $"已选目标技能：{TargetIndependentSkills.Count}";

    // Independent Agenda commands
    [RelayCommand]
    public void ResetAgenda()
    {
        IndependentAgendaSelectionsText = string.Empty;
    }

    private void OnAgendaRaceToggled(string raceKey, bool isSelected)
    {
        var current = ParseParts(IndependentAgendaSelectionsText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (isSelected) current.Add(raceKey);
        else current.Remove(raceKey);
        IndependentAgendaSelectionsText = string.Join(Environment.NewLine, current);
    }

    private void RefreshAgendaRaceSelection()
    {
        var current = ParseParts(IndependentAgendaSelectionsText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _richFilteredAgendaRaces)
        {
            item.IsSelected = current.Contains(item.Race.Key);
        }
    }

    // Filter Helpers
    private void FilterRichTrainees()
    {
        var query = TraineeSearchText.Trim();
        _richFilteredTrainees = UmaCatalogService.Trainees
            .Where(t => string.IsNullOrWhiteSpace(query)
                || t.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.NameJp.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void FilterRichSupportCards()
    {
        var query = SupportCardSearchText.Trim();
        var type = ManualSupportCardTypeFilter;
        _richFilteredSupportCards = UmaCatalogService.SupportCards
            .Where(c => (string.IsNullOrWhiteSpace(query) || c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                && (type == "全部" || c.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void FilterRichFriendCards()
    {
        var query = FriendCardSearchText.Trim();
        var type = FriendCardTypeFilter;
        _richFilteredFriendCards = UmaCatalogService.SupportCards
            .Where(c => (string.IsNullOrWhiteSpace(query) || c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                && (type == "全部" || c.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void FilterRichNormalSkills()
    {
        var query = NormalSkillSearchText.Trim();
        _richFilteredNormalSkills = UmaCatalogService.Skills
            .Where(s => string.IsNullOrWhiteSpace(query) || s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void FilterRichIndependentSkills()
    {
        var query = IndependentSkillSearchText.Trim();
        _richFilteredIndependentSkills = UmaCatalogService.Skills
            .Where(s => string.IsNullOrWhiteSpace(query) || s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void FilterRichAgendaRaces()
    {
        var query = AgendaSearchText.Trim();
        var year = AgendaYearFilter;
        var surface = AgendaSurfaceFilter;
        var dist = AgendaDistanceFilter;
        var grade = AgendaGradeFilter;

        var selectedKeys = ParseParts(IndependentAgendaSelectionsText).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _richFilteredAgendaRaces = UmaCatalogService.IndependentRaces
            .Where(r => (string.IsNullOrWhiteSpace(query) || r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                && (year == "全部" || r.Year.Equals(year, StringComparison.OrdinalIgnoreCase))
                && (surface == "全部" || r.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase))
                && (dist == "全部" || r.Distance.Equals(dist, StringComparison.OrdinalIgnoreCase))
                && (grade == "全部" || r.Grade.Equals(grade, StringComparison.OrdinalIgnoreCase)))
            .Select(r => new IndependentAgendaRaceItemViewModel(r, selectedKeys.Contains(r.Key), OnAgendaRaceToggled))
            .ToList();
    }

    private static string AppendId(string existing, string id)
    {
        var values = ParseParts(existing).ToList();
        if (!values.Contains(id, StringComparer.OrdinalIgnoreCase))
            values.Add(id);
        return string.Join(", ", values);
    }

    private void SetString(string key, string? value) => UpdateSetting(key, JsonValue.Create(value ?? string.Empty));

    private void SetBool(string key, bool value) => UpdateSetting(key, JsonValue.Create(value));

    private void SetNullableInt(string key, string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            UpdateSetting(key, null);
        else if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            UpdateSetting(key, JsonValue.Create(parsed));
    }

    private void SetInt(string key, string? value, int minimum, int maximum)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            UpdateSetting(key, JsonValue.Create(Math.Clamp(parsed, minimum, maximum)));
    }

    private void SetRatio(string key, string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return;
        var ratio = _task.Settings["normalTrainingRatio"] as JsonObject ?? new JsonObject();
        ratio[key] = JsonValue.Create(Math.Clamp(parsed, 0, 100));
        UpdateSetting("normalTrainingRatio", ratio);
    }

    private void SetIntArray(string key, string? value)
    {
        var values = ParseParts(value)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Select(id => (JsonNode?)JsonValue.Create(id));
        UpdateSetting(key, new JsonArray(values.ToArray()));
    }

    private void SetStringArray(string key, string? value)
    {
        var values = ParseParts(value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(item => (JsonNode?)JsonValue.Create(item));
        UpdateSetting(key, new JsonArray(values.ToArray()));
    }

    private void UpdateSetting(string key, JsonNode? value)
    {
        if (_isUpdating)
            return;
        _task.Settings[key] = value?.DeepClone();
        RefreshRawJson();
        NotifyAllTypedProperties();
        NotifyValidationChanged();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string ReadString(string key, string fallback)
    {
        try { return _task.Settings[key]?.GetValue<string>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private bool ReadBool(string key, bool fallback)
    {
        try { return _task.Settings[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private int ReadInt(string key, int fallback)
    {
        try { return _task.Settings[key]?.GetValue<int>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private string ReadNullableInt(string key)
    {
        try { return _task.Settings[key]?.GetValue<int>().ToString(CultureInfo.InvariantCulture) ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
        catch (FormatException) { return string.Empty; }
    }

    private int ReadRatio(string key, int fallback)
    {
        try { return (_task.Settings["normalTrainingRatio"] as JsonObject)?[key]?.GetValue<int>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private string ReadIntArray(string key)
    {
        if (_task.Settings[key] is not JsonArray array)
            return string.Empty;
        return string.Join(", ", array.Select(item =>
        {
            try { return item?.GetValue<int>().ToString(CultureInfo.InvariantCulture); }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }).Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private string ReadStringArray(string key)
    {
        if (_task.Settings[key] is not JsonArray array)
            return string.Empty;
        return string.Join(Environment.NewLine, array.Select(item =>
        {
            try { return item?.GetValue<string>(); }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }).Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string[] ParseParts(string? value) => (value ?? string.Empty)
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static TaskCatalogOption[] Filter(IReadOnlyList<TaskCatalogOption> options, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return options.ToArray();
        return options.Where(option => option.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || option.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsValid));
    }

    private string GetValidationMessage()
    {
        if (!HasValidFieldTypes())
            return "配置中有字段类型不正确，请检查高级 JSON。";
        if (IsStartGame && string.IsNullOrWhiteSpace(PackageId))
            return "请输入游戏包名。";

        if (IsDefinitionTask && !IsResourceFile(DefinitionPath))
            return "管线文件不存在或路径无效。";

        if (IsDailyRace)
        {
            if (!int.TryParse(RaceCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dailyRaceCount) || dailyRaceCount is < 1 or > 6)
                return "每日竞赛数量必须是 1 到 6。";
            if (!DailyRaceModes.Contains(DailyRaceMode, StringComparer.OrdinalIgnoreCase))
                return "每日竞赛奖励类型不受支持。";
            if (!DailyRaceDifficulties.Contains(DailyRaceDifficulty, StringComparer.OrdinalIgnoreCase))
                return "每日竞赛难度不受支持。";
            if (!string.IsNullOrWhiteSpace(TraineeIdText) && !TraineeOptions.Any(option => option.Id == TraineeIdText))
                return "所选马娘 ID 不在当前可用数据库中。";
        }

        if (IsTeamRace && (!int.TryParse(RaceCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var teamRaceCount) || teamRaceCount is < 1 or > 5))
            return "队伍竞技场数量必须是 1 到 5。";

        if (!IsCareerTraining)
            return string.Empty;

        if (!IsResourceFile(ManifestPath)) return "育成剧本 Manifest 文件不存在或路径无效。";
        if (!CareerModes.Contains(CareerMode, StringComparer.OrdinalIgnoreCase)) return "育成模式不受支持。";
        if (!int.TryParse(RunCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var runCount) || runCount < 1)
            return "育成轮数必须是正整数。";

        if (CareerMode.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(StrategyId)) return "请输入策略 ID。";
            if (!LineupStrategies.Contains(NormalLineupStrategy, StringComparer.OrdinalIgnoreCase)) return "所选跑法不受支持。";
            if (SpeedRatio + StaminaRatio + PowerRatio + GutsRatio + WitRatio <= 0)
                return "训练权重总和必须大于 0。";
            if (NormalTrainingStrategy.Equals("custom-ratio", StringComparison.OrdinalIgnoreCase) && TotalRatio <= 0)
                return "自定义训练比例总和必须大于 0。";
        }

        if (CareerMode.Equals("independent", StringComparison.OrdinalIgnoreCase))
        {
            if (!TrainingFocuses.Contains(IndependentTrainingFocus, StringComparer.OrdinalIgnoreCase)) return "训练侧重不受支持。";
            if (!LineupStrategies.Contains(IndependentLineupStrategy, StringComparer.OrdinalIgnoreCase)) return "独立育成跑法不受支持。";
        }

        if (SupportDeckMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var requiredTypes = GetRequiredSupportTypes(SupportDeckPreset);
        if (SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(FriendSupportCardIdText))
            {
                var friend = SupportCardOptions.FirstOrDefault(option => option.Id == FriendSupportCardIdText);
                if (friend is null) return "好友支援卡 ID 无效或不可用。";
                if (requiredTypes is not null && !requiredTypes.ContainsKey("Friend")
                    && (!requiredTypes.TryGetValue(friend.Category ?? string.Empty, out var required) || required == 0))
                    return "好友支援卡类型与所选预设不匹配。";
            }
            return string.Empty;
        }

        var ids = ParseParts(SupportCardIdsText).ToArray();
        var friendId = FriendSupportCardIdText;
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            return "支援卡列表中有重复 ID。";
        if (ids.Length is not (5 or 6) || (!string.IsNullOrWhiteSpace(friendId) && ids.Length != 5))
            return "自选卡组需要 5 张卡加可选好友卡，或不带好友卡时使用 6 张卡。";

        var selected = ids.Select(id => SupportCardOptions.FirstOrDefault(option => option.Id == id)).ToArray();
        if (selected.Any(option => option is null)) return "支援卡列表包含无效或不可用的 ID。";
        var deckTypes = selected.Select(option => option!.Category ?? string.Empty).ToList();
        if (!string.IsNullOrWhiteSpace(friendId))
        {
            if (ids.Contains(friendId, StringComparer.OrdinalIgnoreCase)) return "好友支援卡不能与卡组中的支援卡重复。";
            var friend = SupportCardOptions.FirstOrDefault(option => option.Id == friendId);
            if (friend is null) return "好友支援卡 ID 无效或不可用。";
            if (requiredTypes is not null && !requiredTypes.ContainsKey("Friend")
                && (!requiredTypes.TryGetValue(friend.Category ?? string.Empty, out var required) || required == 0))
                return "好友支援卡类型与所选预设不匹配。";
            deckTypes.Add(friend.Category ?? string.Empty);
        }

        if (requiredTypes is not null)
        {
            var actual = deckTypes.GroupBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            if (!requiredTypes.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value)
                || actual.Values.Sum() != 6 - (string.IsNullOrWhiteSpace(friendId) ? 0 : 1))
                return "所选支援卡类型不符合当前卡组预设。";
        }
        return string.Empty;
    }

    private static Dictionary<string, int>? GetRequiredSupportTypes(string preset) => preset switch
    {
        "speed3-stamina3" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Speed"] = 3, ["Stamina"] = 3 },
        "speed3-stamina2-wit1" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Speed"] = 3, ["Stamina"] = 2, ["Wit"] = 1 },
        "speed2-stamina2-power1-wit1" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Speed"] = 2, ["Stamina"] = 2, ["Power"] = 1, ["Wit"] = 1 },
        "speed2-stamina1-power1-wit1-friend1" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Speed"] = 2, ["Stamina"] = 1, ["Power"] = 1, ["Wit"] = 1, ["Friend"] = 1 },
        _ => null,
    };

    private void RefreshRawJson()
    {
        _isUpdating = true;
        _rawSettingsJson = _task.Settings.ToJsonString(JsonOptions);
        OnPropertyChanged(nameof(RawSettingsJson));
        _isUpdating = false;
    }

    private void NotifyAllTypedProperties()
    {
        foreach (var property in new[]
        {
            nameof(PackageId), nameof(ActivityName), nameof(DefinitionPath), nameof(RaceCountText),
            nameof(StopWhenTicketsEmpty), nameof(DailyRaceMode), nameof(DailyRaceDifficulty),
            nameof(TraineeIdText), nameof(SelectedTraineeItem), nameof(ManifestPath), nameof(CareerMode),
            nameof(IsNormalCareer), nameof(IsIndependentCareer), nameof(RunCountText),
            nameof(StrategyId), nameof(NormalLineupStrategy), nameof(NormalTrainingStrategy),
            nameof(IsCustomNormalTrainingStrategy), nameof(NormalTrainingSpeedText),
            nameof(NormalTrainingStaminaText), nameof(NormalTrainingPowerText), nameof(NormalTrainingGutsText),
            nameof(NormalTrainingWitText), nameof(RetryFailedRaceWithAlarmClock), nameof(NormalEventHandling),
            nameof(NormalSkillIdsText), nameof(IndependentTrainingFocus), nameof(IndependentLineupStrategy),
            nameof(IndependentAgendaSelectionsText), nameof(IndependentSkillIdsText),
            nameof(DeleteExistingCareerData), nameof(AutoRestoreTp), nameof(SupportDeckMode),
            nameof(IsManualSupportDeck), nameof(IsSupportPresetMode), nameof(SupportDeckPreset),
            nameof(FriendSupportCardIdText), nameof(SelectedFriendCard), nameof(SupportCardIdsText),
            nameof(PauseOnUnknownOutcome), nameof(AllowOptionalRaces), nameof(LegacySelectionMode),
            nameof(IsManualLegacySelection), nameof(UseLegacyGuest), nameof(UseCachedLegacy),
            nameof(LegacyAttributeSparksText), nameof(LegacyAptitudeSparksText), nameof(IsStartGame),
            nameof(IsCareerTraining), nameof(IsDailyRace), nameof(IsTeamRace), nameof(IsDefinitionTask),
            nameof(IsSimpleCollectionTask), nameof(SelectedTraineeOption), nameof(SelectedNormalSkillOption),
            nameof(SelectedIndependentSkillOption), nameof(SelectedSupportCardOption),
            nameof(SelectedFriendSupportCardOption), nameof(SelectedIndependentAgendaRaceOption),
            nameof(SelectedSupportDeckPreset), nameof(FilteredTraineeOptions),
            nameof(FilteredSupportCardOptions), nameof(FilteredSkillOptions),
            nameof(FilteredIndependentRaceOptions), nameof(RichFilteredTrainees),
            nameof(RichFilteredSupportCards), nameof(RichFilteredFriendCards),
            nameof(RichFilteredNormalSkills), nameof(RichFilteredIndependentSkills),
            nameof(RichFilteredAgendaRaces), nameof(SelectedSupportCardCountText),
            nameof(TargetNormalSkillCountText), nameof(TargetIndependentSkillCountText),
            nameof(SelectedAgendaRaceCountText), nameof(SelectedCareerModeOption),
            nameof(SelectedSupportDeckModeOption), nameof(SelectedLegacySelectionModeOption),
            nameof(SelectedNormalTrainingStrategyOption), nameof(SelectedNormalLineupStrategyOption),
            nameof(SelectedIndependentLineupStrategyOption), nameof(SelectedIndependentTrainingFocusOption),
            nameof(SelectedDailyRaceModeOption), nameof(SelectedDailyRaceDifficultyOption),
        })
        {
            OnPropertyChanged(property);
        }

        NotifyRatioUpdated();
        NotifyAttributeSparksChanged();
        NotifyAptitudeSparksChanged();
    }

    private bool HasValidFieldTypes()
    {
        try
        {
            _ = PackageId;
            _ = ActivityName;
            _ = DefinitionPath;
            _ = RaceCountText;
            _ = StopWhenTicketsEmpty;
            _ = DailyRaceMode;
            _ = DailyRaceDifficulty;
            _ = TraineeIdText;
            _ = ManifestPath;
            _ = CareerMode;
            _ = RunCountText;
            _ = StrategyId;
            _ = NormalLineupStrategy;
            _ = NormalTrainingSpeedText;
            _ = NormalTrainingStaminaText;
            _ = NormalTrainingPowerText;
            _ = NormalTrainingGutsText;
            _ = NormalTrainingWitText;
            _ = RetryFailedRaceWithAlarmClock;
            _ = NormalEventHandling;
            _ = NormalSkillIdsText;
            _ = IndependentTrainingFocus;
            _ = IndependentLineupStrategy;
            _ = IndependentAgendaSelectionsText;
            _ = IndependentSkillIdsText;
            _ = DeleteExistingCareerData;
            _ = AutoRestoreTp;
            _ = SupportDeckMode;
            _ = SupportDeckPreset;
            _ = FriendSupportCardIdText;
            _ = SupportCardIdsText;
            _ = PauseOnUnknownOutcome;
            _ = AllowOptionalRaces;
            _ = LegacySelectionMode;
            _ = UseLegacyGuest;
            _ = UseCachedLegacy;
            _ = LegacyAttributeSparksText;
            _ = LegacyAptitudeSparksText;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsResourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = PlatformPaths.ResolveActiveResourcePath(path);
        return File.Exists(full);
    }
}

public sealed record TaskCatalogOption(string Id, string DisplayName, string? Category = null);
public sealed record TaskSettingOption(string Value, string Label);

internal static class TaskCatalogData
{
    public static TaskCatalogOption[] LoadTrainees() => LoadRows(
        Path.Combine("uma", "database", "global", "trainees.json"),
        null,
        item => (ReadString(item, "trainee_id"), ReadString(item, "name_en")),
        availableOnly: true);

    public static TaskCatalogOption[] LoadSupportCards() => LoadRawRows(
            Path.Combine("uma", "database", "global", "support_cards.json"), null)
        .Where(item => ReadBool(item, "available", true))
        .Select(item => new TaskCatalogOption(
            ReadString(item, "support_card_id"),
            $"{ReadString(item, "name_en")}  (#{ReadString(item, "support_card_id")})",
            ReadString(item, "type")))
        .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.DisplayName))
        .ToArray();

    public static TaskCatalogOption[] LoadSkills() => LoadRows(
        Path.Combine("uma", "database", "global", "skills.json"),
        "skills",
        item => (ReadString(item, "skillId"), ReadString(item, "skillName")),
        availableOnly: true,
        singleModeOnly: true);

    public static TaskCatalogOption[] LoadIndependentRaces()
    {
        var rows = LoadRawRows(Path.Combine("hachimi", "ura", "independent_training", "races.global.json"), "races");
        return rows.Where(item => ReadBool(item, "gameAvailable", true)).Select(item =>
        {
            var year = ReadString(item, "year");
            var turn = ReadString(item, "turn");
            var name = ReadString(item, "raceName");
            var key = string.Join("|", year, turn, name);
            var display = $"{year} · {turn} · {name} ({ReadString(item, "grade")}, {ReadString(item, "lengthM")})";
            return new TaskCatalogOption(key, display);
        }).ToArray();
    }

    private static TaskCatalogOption[] LoadRows(
        string relativePath,
        string? arrayProperty,
        Func<JsonElement, (string Id, string Name)> read,
        bool availableOnly,
        bool singleModeOnly = false)
    {
        var rows = LoadRawRows(relativePath, arrayProperty);
        return rows
            .Where(item => !availableOnly || ReadBool(item, "availableInGlobal", ReadBool(item, "available", true)))
            .Where(item => !singleModeOnly || ReadBool(item, "singleModeEnabled", false))
            .Select(read)
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new TaskCatalogOption(item.Id, $"{item.Name}  (#{item.Id})"))
            .ToArray();
    }

    private static JsonElement[] LoadRawRows(string relativePath, string? arrayProperty)
    {
        try
        {
            var resourceRoot = PlatformPaths.GetActiveResourceDirectory();
            var filePath = Path.Combine(resourceRoot, relativePath);
            if (!File.Exists(filePath))
                return [];
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = document.RootElement;
            if (arrayProperty is not null && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(arrayProperty, out var array))
                root = array;
            return root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()).ToArray()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string ReadString(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool ReadBool(JsonElement item, string key, bool fallback)
    {
        if (item.TryGetProperty(key, out var value)
            && (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return value.GetBoolean();
        return fallback;
    }
}
