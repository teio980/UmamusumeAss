using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Umamusume.AppCore.Models;

public sealed class ConnectionSettings
{
    public const int MaxAutoStartEmulatorWaitSeconds = 300;

    public static IReadOnlyList<string> SupportedConnectConfigs { get; } =
    [
        "General",
        "MuMuEmulator12",
        "LDPlayer",
        "BlueStacks",
        "Nox",
        "XYAZ",
        "WSA",
        "Androws",
    ];

    public string AdbPath { get; set; } = "";

    public string ConnectAddress { get; set; } = "";

    public bool AutoDetectConnection { get; set; } = true;

    public bool AlwaysAutoDetectConnection { get; set; }

    public bool AutoStartEmulator { get; set; }

    public string EmulatorExecutablePath { get; set; } = "";

    private int _autoStartEmulatorWaitSeconds = 5;

    public int AutoStartEmulatorWaitSeconds
    {
        get => _autoStartEmulatorWaitSeconds;
        set => _autoStartEmulatorWaitSeconds = Math.Clamp(
            value,
            0,
            MaxAutoStartEmulatorWaitSeconds);
    }

    private static readonly HashSet<string> KnownConnectConfigs =
        new(SupportedConnectConfigs, StringComparer.Ordinal);

    private string _connectConfig = "General";

    public string ConnectConfig
    {
        get => _connectConfig;
        set => _connectConfig = value is not null && KnownConnectConfigs.Contains(value) ? value : "General";
    }

    public string Language { get; set; } = "en-US";

    private string _theme = "System";

    public string Theme
    {
        get => _theme;
        set => _theme = value?.Trim().ToLowerInvariant() switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System",
        };
    }

    private int _connectionTimeoutSeconds = 15;

    public int ConnectionTimeoutSeconds
    {
        get => _connectionTimeoutSeconds;
        set => _connectionTimeoutSeconds = Math.Clamp(value, 1, 300);
    }

    private HachimiSettings _hachimi = new();

    public HachimiSettings Hachimi
    {
        get => _hachimi;
        set => _hachimi = value ?? new HachimiSettings();
    }

    public List<string> ConnectAddressHistory { get; set; } = [];

    public List<string> TargetPackageIds { get; set; } = [];

    private List<HachimiTaskQueueItem>? _taskQueue;

    /// <summary>
    /// Compatibility field used by the Windows settings file. AppCore keeps
    /// the editable task profiles under Hachimi.TaskQueue and mirrors them here
    /// when loading and saving so both applications read the same queue.
    /// </summary>
    public List<HachimiTaskQueueItem> TaskQueue
    {
        get => _taskQueue ?? Hachimi.TaskQueue;
        set => _taskQueue = value ?? [];
    }

    [JsonIgnore]
    internal bool HasWindowsTaskQueue => _taskQueue is not null;

    public bool StartupUpdateCheck { get; set; } = true;

    public string? SkippedProgramVersion { get; set; }

    public string TargetActivityName { get; set; } = "";

    public void SynchronizeTaskQueue(bool preferWindowsField = false)
    {
        if (preferWindowsField && _taskQueue is not null)
        {
            Hachimi.TaskQueue = CloneTaskQueue(_taskQueue);
            return;
        }

        TaskQueue = CloneTaskQueue(Hachimi.TaskQueue);
    }

    private static List<HachimiTaskQueueItem> CloneTaskQueue(
        IEnumerable<HachimiTaskQueueItem> tasks) => tasks.Select(task => new HachimiTaskQueueItem
        {
            TaskId = task.TaskId,
            IsEnabled = task.IsEnabled,
            Settings = (System.Text.Json.Nodes.JsonObject)task.Settings.DeepClone(),
        }).ToList();
}
