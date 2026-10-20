using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;

namespace Umamusume.Avalonia.ViewModels;

public enum TaskItemStatus
{
    Idle,
    Queued,
    Disabled,
    Running,
    Completed,
    Failed,
    Canceled,
    Skipped
}

/// <summary>A single editable task profile in the persistent Hachimi queue.</summary>
public sealed partial class TaskQueueItemViewModel : ObservableObject
{
    private readonly UmaTaskDefinition _definition;
    private readonly ILocalizationService? _localizationService;
    private JsonObject _settings;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isSelected;

    private TaskItemStatus _status = TaskItemStatus.Idle;
    private string? _progressSuffix;

    public string DisplayStatus
    {
        get
        {
            var isEn = _localizationService?.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
            var baseStatus = _status switch
            {
                TaskItemStatus.Idle => isEn ? "Pending" : "待运行",
                TaskItemStatus.Queued => isEn ? "Queued" : "排队中",
                TaskItemStatus.Disabled => isEn ? "Disabled" : "已停用",
                TaskItemStatus.Running => isEn ? "Running" : "运行中",
                TaskItemStatus.Completed => isEn ? "Completed" : "已完成",
                TaskItemStatus.Failed => isEn ? "Failed" : "失败",
                TaskItemStatus.Canceled => isEn ? "Canceled" : "已取消",
                TaskItemStatus.Skipped => isEn ? "Skipped" : "已跳过",
                _ => isEn ? "Pending" : "待运行"
            };
            return string.IsNullOrEmpty(_progressSuffix) ? baseStatus : $"{baseStatus} · {_progressSuffix}";
        }
    }

    public TaskItemStatus Status => _status;

    public ILocalizationService? LocalizationService => _localizationService;

    public string TaskId => _definition.Id;

    public string Name => TaskLabels.GetName(TaskId, _definition.Name, _localizationService?.CurrentLanguage);

    public string Description => TaskLabels.GetDescription(TaskId, _definition.Description, _localizationService?.CurrentLanguage);

    public TaskSettingsViewModel SettingsEditor { get; }

    public JsonObject Settings => _settings;

    public event EventHandler? SettingsChanged;

    public TaskQueueItemViewModel(UmaTaskDefinition definition, bool isEnabled, JsonObject? settings, ILocalizationService? localizationService = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _localizationService = localizationService;
        _isEnabled = isEnabled;
        _settings = settings?.DeepClone().AsObject() ?? new JsonObject();
        SettingsEditor = new TaskSettingsViewModel(this);
        SettingsEditor.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public TaskQueueItemViewModel Clone() => new(_definition, IsEnabled, _settings, _localizationService);

    internal void RefreshLabels()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplayStatus));
        SettingsEditor.RefreshLanguage();
    }

    internal void ReplaceSettings(JsonObject settings)
    {
        _settings = settings;
        OnPropertyChanged(nameof(Settings));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifySettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public void SetStatus(TaskItemStatus status, string? progressSuffix = null)
    {
        _status = status;
        _progressSuffix = progressSuffix;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DisplayStatus));
    }

    public void SetRunStatus(string status)
    {
        if (status.Contains("运行", StringComparison.OrdinalIgnoreCase) || status.Contains("Run", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = status.Replace("运行中", "").Replace("Running", "").Trim('·', ' ');
            SetStatus(TaskItemStatus.Running, string.IsNullOrEmpty(suffix) ? null : suffix);
        }
        else if (status.Contains("完成", StringComparison.OrdinalIgnoreCase) || status.Contains("Complete", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Completed);
        }
        else if (status.Contains("失败", StringComparison.OrdinalIgnoreCase) || status.Contains("Fail", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Failed);
        }
        else if (status.Contains("取消", StringComparison.OrdinalIgnoreCase) || status.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Canceled);
        }
        else if (status.Contains("跳过", StringComparison.OrdinalIgnoreCase) || status.Contains("Skip", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Skipped);
        }
        else if (status.Contains("排队", StringComparison.OrdinalIgnoreCase) || status.Contains("Queue", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Queued);
        }
        else if (status.Contains("停用", StringComparison.OrdinalIgnoreCase) || status.Contains("Disable", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(TaskItemStatus.Disabled);
        }
        else
        {
            SetStatus(TaskItemStatus.Idle);
        }
    }
}

internal static class TaskLabels
{
    private static readonly IReadOnlyDictionary<string, string> EnglishNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start-game"] = "Start game", ["career-training"] = "Career training", ["daily-race"] = "Daily race",
        ["team-race"] = "Team race", ["mail-collection"] = "Collect mail rewards",
        ["mission-collection"] = "Collect mission rewards", ["shop-purchase"] = "Friends & shop",
    };

    private static readonly IReadOnlyDictionary<string, string> EnglishDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start-game"] = "Launch the configured Android game and wait for the home screen.",
        ["career-training"] = "Run one or more training careers with the selected scenario and strategy.",
        ["daily-race"] = "Automatically enter Daily Race with the selected reward and difficulty.",
        ["team-race"] = "Run Team Race and manage remaining tickets.",
        ["mail-collection"] = "Collect available presents from the in-game mailbox.",
        ["mission-collection"] = "Collect completed mission rewards.",
        ["shop-purchase"] = "Purchase configured items from friends and shops.",
    };

    public static string GetName(string id, string fallback, string? language = null)
    {
        if (IsEnglish(language)) return EnglishNames.GetValueOrDefault(id, fallback);
        return id switch
        {
            "start-game" => "启动游戏", "career-training" => "育成训练", "daily-race" => "每日竞赛",
            "team-race" => "队伍竞技场", "mail-collection" => "领取邮箱奖励",
            "mission-collection" => "领取任务奖励", "shop-purchase" => "好友与商店", _ => fallback,
        };
    }

    public static string GetDescription(string id, string fallback, string? language = null)
    {
        if (IsEnglish(language)) return EnglishDescriptions.GetValueOrDefault(id, fallback);
        return id switch
        {
            "start-game" => "启动已配置的 Android 游戏，并等待首页就绪。",
            "career-training" => "按育成剧本和策略执行一轮或多轮育成。",
            "daily-race" => "按奖励类型与难度自动进行每日竞赛。",
            "team-race" => "自动执行队伍竞技场并管理剩余入场券。",
            "mail-collection" => "领取游戏邮箱中的可用礼物。",
            "mission-collection" => "领取已完成的任务奖励。",
            "shop-purchase" => "使用全局 Hachimi 商店购买设置。",
            _ => fallback,
        };
    }

    private static bool IsEnglish(string? language) => language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record TaskTypeOption(string Id, string Name, string Description)
{
    public override string ToString() => Name;

    public static IReadOnlyList<TaskTypeOption> All { get; } = Build("zh-CN");

    public static IReadOnlyList<TaskTypeOption> Build(string? language) => UmaTaskCatalog.All
        .Select(item => new TaskTypeOption(
            item.Id,
            TaskLabels.GetName(item.Id, item.Name, language),
            TaskLabels.GetDescription(item.Id, item.Description, language)))
        .ToArray();
}
