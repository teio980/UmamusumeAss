using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;





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


    private HachimiSettings _hachimi = new();


    public HachimiSettings Hachimi
    {
        get => _hachimi;
        set => _hachimi = value ?? new HachimiSettings();
    }


    public List<string> ConnectAddressHistory { get; set; } = [];


    public List<string> TargetPackageIds { get; set; } = [];





    public string TargetActivityName { get; set; } = "";


    public List<GrassTaskCacheItem> TaskQueue { get; set; } = [];





    public void AddAddressToHistory(string address)
    {
        if (string.IsNullOrEmpty(address))
            return;

        ConnectAddressHistory.Remove(address);
        ConnectAddressHistory.Insert(0, address);

        if (ConnectAddressHistory.Count > 5)
            ConnectAddressHistory.RemoveAt(ConnectAddressHistory.Count - 1);
    }
}
