namespace UmamusumeWpfGui.Helper;

internal static class EmulatorProfileCatalog
{
    private static readonly Dictionary<string, EmulatorProfileDefinition> ProfilesByProcess =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HD-Player"] = new(
                "BlueStacks",
                ["HD-Adb.exe", @"Engine\ProgramFiles\HD-Adb.exe"],
                ["127.0.0.1:5555", "127.0.0.1:5556", "127.0.0.1:5565", "127.0.0.1:5575", "127.0.0.1:5585", "127.0.0.1:5595", "127.0.0.1:5554"]),
            ["dnplayer"] = new(
                "LDPlayer",
                ["adb.exe"],
                ["emulator-5554", "emulator-5556", "emulator-5558", "emulator-5560", "127.0.0.1:5555", "127.0.0.1:5557", "127.0.0.1:5559", "127.0.0.1:5561"]),
            ["Nox"] = new("Nox", ["nox_adb.exe"], ["127.0.0.1:62001", "127.0.0.1:59865"]),
            ["MuMuPlayer"] = MuMuProfile(),
            ["MuMuNxDevice"] = MuMuProfile(),
            ["MEmu"] = new("XYAZ", ["adb.exe"], ["127.0.0.1:21503"]),
        };

    private static readonly Dictionary<string, IReadOnlyList<string>> FallbackEndpoints = CreateFallbackEndpoints();

    public static bool TryGetForProcess(string processName, out EmulatorProfileDefinition profile) =>
        ProfilesByProcess.TryGetValue(processName, out profile!);

    public static IReadOnlyList<string> GetFallbackEndpoints(string profileName) =>
        FallbackEndpoints.TryGetValue(profileName, out var endpoints)
            ? endpoints
            : [];

    private static EmulatorProfileDefinition MuMuProfile() =>
        new(
            "MuMuEmulator12",
            [
                @"..\..\..\nx_main\adb.exe",
                @"..\vmonitor\bin\adb_server.exe",
                @"..\..\MuMu\emulator\nemu\vmonitor\bin\adb_server.exe",
                "adb.exe",
            ],
            ["127.0.0.1:16384", "127.0.0.1:16416", "127.0.0.1:16448", "127.0.0.1:16480", "127.0.0.1:16512", "127.0.0.1:16544", "127.0.0.1:16576"]);

    private static Dictionary<string, IReadOnlyList<string>> CreateFallbackEndpoints()
    {
        var endpoints = ProfilesByProcess.Values
            .GroupBy(profile => profile.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().FallbackEndpoints, StringComparer.Ordinal);
        endpoints["Androws"] = ["127.0.0.1:5555"];
        endpoints["WSA"] = ["127.0.0.1:58526"];
        return endpoints;
    }
}

internal sealed record EmulatorProfileDefinition(
    string Name,
    IReadOnlyList<string> AdbCandidates,
    IReadOnlyList<string> FallbackEndpoints);
