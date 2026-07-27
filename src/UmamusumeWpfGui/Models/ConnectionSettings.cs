using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Persisted connection configuration.
/// S1 mode restricts <see cref="ConnectConfig"/> to "General" only.
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>Path to the ADB executable.</summary>
    public string AdbPath { get; set; } = "";

    /// <summary>Last used ADB connect address (ip:port).</summary>
    public string ConnectAddress { get; set; } = "";

    /// <summary>Whether to automatically detect emulators.</summary>
    public bool AutoDetectConnection { get; set; } = true;

    /// <summary>Whether auto-detect should run on every connect.</summary>
    public bool AlwaysAutoDetectConnection { get; set; }

    private string _connectConfig = "General";

    /// <summary>
    /// Connection profile. In S1 mode, only "General" is accepted;
    /// any unknown, empty, or null value silently falls back to "General".
    /// </summary>
    public string ConnectConfig
    {
        get => _connectConfig;
        set => _connectConfig = value is "General" ? value : "General";
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
