using System;
using UmamusumeWpfGui.Helper;
using Xunit;

namespace UmamusumeWpfGui.Tests.Helper;

public sealed class EmulatorProfileCatalogTests
{
    [Theory]
    [InlineData("BlueStacks", "127.0.0.1:5555")]
    [InlineData("MuMuEmulator12", "127.0.0.1:16384")]
    [InlineData("LDPlayer", "emulator-5554")]
    [InlineData("Nox", "127.0.0.1:62001")]
    [InlineData("XYAZ", "127.0.0.1:21503")]
    [InlineData("Androws", "127.0.0.1:5555")]
    [InlineData("WSA", "127.0.0.1:58526")]
    public void GetFallbackEndpoints_WhenProfileIsSupported_ReturnsMaaCompatibleFirstEndpoint(
        string profileName,
        string expectedEndpoint)
    {



        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints(profileName);


        Assert.Equal(expectedEndpoint, endpoints[0]);
    }

    [Fact]
    public void GetFallbackEndpoints_WhenProfileIsMuMu_ReturnsFiniteKnownEndpoints()
    {



        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints("MuMuEmulator12");


        Assert.Equal(
            [
                "127.0.0.1:16384",
                "127.0.0.1:16416",
                "127.0.0.1:16448",
                "127.0.0.1:16480",
                "127.0.0.1:16512",
                "127.0.0.1:16544",
                "127.0.0.1:16576",
            ],
            endpoints);
    }

    [Theory]
    [InlineData("MuMuNxMain")]
    [InlineData("MuMuPlayer")]
    [InlineData("MuMuNxDevice")]
    public void MuMuProcessAliases_MapToMuMuProfile(string processName)
    {
        Assert.True(EmulatorProfileCatalog.TryGetForProcess(processName, out var profile));
        Assert.Equal("MuMuEmulator12", profile.Name);
    }

    [Fact]
    public void GetFallbackEndpoints_WhenProfileIsUnknown_ReturnsNoEndpoints()
    {



        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints("Unknown");


        Assert.Empty(endpoints);
    }
}
