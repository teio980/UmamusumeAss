using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Persisted connection configuration.
/// Connection profiles are validated against the supported MAA-style profile names.
/// </summary>
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

    /// <summary>Path to the ADB executable.</summary>
    public string AdbPath { get; set; } = "";

    /// <summary>Last used ADB connect address (ip:port).</summary>
    public string ConnectAddress { get; set; } = "";

    /// <summary>Whether to automatically detect emulators.</summary>
    public bool AutoDetectConnection { get; set; } = true;

    /// <summary>Whether auto-detect should run on every connect.</summary>
    public bool AlwaysAutoDetectConnection { get; set; }

    /// <summary>Whether a configured emulator should start when Connect finds none running.</summary>
    public bool AutoStartEmulator { get; set; }

    /// <summary>Executable or shortcut used to start the emulator.</summary>
    public string EmulatorExecutablePath { get; set; } = "";

    private int _autoStartEmulatorWaitSeconds = 5;

    /// <summary>Seconds to wait after successfully starting an emulator.</summary>
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

    /// <summary>
    /// Connection profile. Preserves known MAA-style profile names;
    /// any unknown, empty, or null value silently falls back to "General".
    /// </summary>
    public string ConnectConfig
    {
        get => _connectConfig;
        set => _connectConfig = value is not null && KnownConnectConfigs.Contains(value) ? value : "General";
    }

    /// <summary>Display language culture code.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>History of successfully connected addresses (max 5).</summary>
    public List<string> ConnectAddressHistory { get; set; } = [];

    /// <summary>Target Android package identifiers.</summary>
    public List<string> TargetPackageIds { get; set; } = [];

    /// <summary>
    /// Adds an address to the history. Blank or null values are ignored.
    /// Existing addresses are moved to the front. History is capped at 5 entries.
    /// </summary>
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
