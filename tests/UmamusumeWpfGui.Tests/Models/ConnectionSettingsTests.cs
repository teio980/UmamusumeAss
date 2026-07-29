using System.Linq;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Models;

public sealed class ConnectionSettingsTests
{
    // ================================================================
    // Defaults
    // ================================================================

    [Fact]
    public void Defaults_AdbPathIsEmpty()
    {
        var s = new ConnectionSettings();
        Assert.Equal("", s.AdbPath);
    }

    [Fact]
    public void Defaults_ConnectAddressIsEmpty()
    {
        var s = new ConnectionSettings();
        Assert.Equal("", s.ConnectAddress);
    }

    [Fact]
    public void Defaults_AutoDetectConnectionIsTrue()
    {
        var s = new ConnectionSettings();
        Assert.True(s.AutoDetectConnection);
    }

    [Fact]
    public void Defaults_AlwaysAutoDetectConnectionIsFalse()
    {
        var s = new ConnectionSettings();
        Assert.False(s.AlwaysAutoDetectConnection);
    }

    [Fact]
    public void Defaults_AutoStartEmulatorIsFalse()
    {
        var s = new ConnectionSettings();

        Assert.False(s.AutoStartEmulator);
        Assert.Equal("", s.EmulatorExecutablePath);
    }

    [Fact]
    public void AutoStartEmulatorWaitSeconds_DefaultsToFive_AndClampsNegativeValues()
    {
        var settings = new ConnectionSettings();

        Assert.Equal(5, settings.AutoStartEmulatorWaitSeconds);

        settings.AutoStartEmulatorWaitSeconds = -1;

        Assert.Equal(0, settings.AutoStartEmulatorWaitSeconds);
    }

    [Fact]
    public void Defaults_ConnectConfigIsGeneral()
    {
        var s = new ConnectionSettings();
        Assert.Equal("General", s.ConnectConfig);
    }

    [Fact]
    public void Defaults_LanguageIsEnUs()
    {
        var s = new ConnectionSettings();
        Assert.Equal("en-US", s.Language);
    }

    [Fact]
    public void Defaults_ConnectAddressHistoryIsEmpty()
    {
        var s = new ConnectionSettings();
        Assert.Empty(s.ConnectAddressHistory);
    }

    [Fact]
    public void Defaults_TargetPackageIdsIsEmpty()
    {
        var s = new ConnectionSettings();
        Assert.Empty(s.TargetPackageIds);
    }

    // ================================================================
    // ConnectConfig — General-only fallback (S1 mode)
    // ================================================================

    [Fact]
    public void ConnectConfig_SetToGeneral_StoresGeneral()
    {
        var s = new ConnectionSettings { ConnectConfig = "General" };
        Assert.Equal("General", s.ConnectConfig);
    }

    [Fact]
    public void ConnectConfig_SetToUnknownValue_FallsBackToGeneral()
    {
        var s = new ConnectionSettings { ConnectConfig = "MuMu12" };
        Assert.Equal("General", s.ConnectConfig);
    }

    [Theory]
    [InlineData("MuMuEmulator12")]
    [InlineData("LDPlayer")]
    [InlineData("BlueStacks")]
    [InlineData("Nox")]
    [InlineData("XYAZ")]
    [InlineData("WSA")]
    [InlineData("Androws")]
    public void ConnectConfig_SetToKnownProfile_PreservesValue(string profileName)
    {
        var s = new ConnectionSettings { ConnectConfig = profileName };
        Assert.Equal(profileName, s.ConnectConfig);
    }

    [Fact]
    public void ConnectConfig_SetToEmptyString_FallsBackToGeneral()
    {
        var s = new ConnectionSettings { ConnectConfig = "" };
        Assert.Equal("General", s.ConnectConfig);
    }

    [Fact]
    public void ConnectConfig_SetToNull_FallsBackToGeneral()
    {
        var s = new ConnectionSettings { ConnectConfig = null! };
        Assert.Equal("General", s.ConnectConfig);
    }

    // ================================================================
    // AddAddressToHistory — cap 5, dedup, blank ignored
    // ================================================================

    [Fact]
    public void AddAddressToHistory_BlankAddress_DoesNotAdd()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("");
        Assert.Empty(s.ConnectAddressHistory);
    }

    [Fact]
    public void AddAddressToHistory_NullAddress_DoesNotAdd()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory(null!);
        Assert.Empty(s.ConnectAddressHistory);
    }

    [Fact]
    public void AddAddressToHistory_FirstAddress_Appends()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("127.0.0.1:5555");
        Assert.Equal(["127.0.0.1:5555"], s.ConnectAddressHistory);
    }

    [Fact]
    public void AddAddressToHistory_MultipleAddresses_NewestFirst()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("addr1");
        s.AddAddressToHistory("addr2");
        s.AddAddressToHistory("addr3");
        Assert.Equal(["addr3", "addr2", "addr1"], s.ConnectAddressHistory);
    }

    [Fact]
    public void AddAddressToHistory_ExistingAddress_MovesToFront()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("addr1");
        s.AddAddressToHistory("addr2");
        s.AddAddressToHistory("addr3");
        s.AddAddressToHistory("addr1"); // existing — move to front
        Assert.Equal(["addr1", "addr3", "addr2"], s.ConnectAddressHistory);
        Assert.Equal(3, s.ConnectAddressHistory.Count);
    }

    [Fact]
    public void AddAddressToHistory_CapsAtFive()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("addr1");
        s.AddAddressToHistory("addr2");
        s.AddAddressToHistory("addr3");
        s.AddAddressToHistory("addr4");
        s.AddAddressToHistory("addr5");

        // 6th addition evicts the last entry (oldest)
        s.AddAddressToHistory("addr6");
        Assert.Equal(5, s.ConnectAddressHistory.Count);
        Assert.DoesNotContain("addr1", s.ConnectAddressHistory);
        Assert.Equal(["addr6", "addr5", "addr4", "addr3", "addr2"],
            s.ConnectAddressHistory);
    }

    [Fact]
    public void AddAddressToHistory_ExistingAtEndMovesToFrontWithinCap()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("addr1");
        s.AddAddressToHistory("addr2");
        s.AddAddressToHistory("addr3");
        s.AddAddressToHistory("addr4");
        s.AddAddressToHistory("addr5");
        // addr1 is oldest — re-adding moves it to front
        s.AddAddressToHistory("addr1");
        Assert.Equal(5, s.ConnectAddressHistory.Count);
        Assert.Equal(["addr1", "addr5", "addr4", "addr3", "addr2"],
            s.ConnectAddressHistory);
    }

    // ================================================================
    // JSON roundtrip via System.Text.Json
    // ================================================================

    [Fact]
    public void JsonRoundtrip_AllPropertiesRoundtrip()
    {
        var s = new ConnectionSettings
        {
            AdbPath = @"C:\adb\adb.exe",
            ConnectAddress = "192.168.1.100:5555",
            AutoDetectConnection = false,
            AlwaysAutoDetectConnection = true,
            AutoStartEmulator = true,
            EmulatorExecutablePath = @"C:\Program Files\Netease\MuMuPlayer\nx_device\15.0\shell\MuMuNxDevice.exe",
            AutoStartEmulatorWaitSeconds = 12,
            ConnectConfig = "General",
            Language = "zh-CN",
        };
        s.AddAddressToHistory("10.0.0.1:5555");
        s.AddAddressToHistory("10.0.0.2:5555");
        s.TargetPackageIds.Add("com.example.app");

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(s.AdbPath, deserialized!.AdbPath);
        Assert.Equal(s.ConnectAddress, deserialized.ConnectAddress);
        Assert.Equal(s.AutoDetectConnection, deserialized.AutoDetectConnection);
        Assert.Equal(s.AlwaysAutoDetectConnection, deserialized.AlwaysAutoDetectConnection);
        Assert.Equal(s.AutoStartEmulator, deserialized.AutoStartEmulator);
        Assert.Equal(s.EmulatorExecutablePath, deserialized.EmulatorExecutablePath);
        Assert.Equal(s.AutoStartEmulatorWaitSeconds, deserialized.AutoStartEmulatorWaitSeconds);
        Assert.Equal(s.ConnectConfig, deserialized.ConnectConfig);
        Assert.Equal(s.Language, deserialized.Language);
        Assert.Equal(s.ConnectAddressHistory, deserialized.ConnectAddressHistory);
        Assert.Equal(s.TargetPackageIds, deserialized.TargetPackageIds);
    }

    [Fact]
    public void JsonRoundtrip_EmptyDefaultsRoundtrip()
    {
        var s = new ConnectionSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("", deserialized!.AdbPath);
        Assert.Equal("", deserialized.ConnectAddress);
        Assert.True(deserialized.AutoDetectConnection);
        Assert.False(deserialized.AlwaysAutoDetectConnection);
        Assert.Equal("General", deserialized.ConnectConfig);
        Assert.Equal("en-US", deserialized.Language);
        Assert.Empty(deserialized.ConnectAddressHistory);
        Assert.Empty(deserialized.TargetPackageIds);
    }

    [Fact]
    public void JsonRoundtrip_HistoryOrderPreserved()
    {
        var s = new ConnectionSettings();
        s.AddAddressToHistory("c");
        s.AddAddressToHistory("b");
        s.AddAddressToHistory("a");
        Assert.Equal(["a", "b", "c"], s.ConnectAddressHistory);

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(["a", "b", "c"], deserialized!.ConnectAddressHistory);
    }
}
