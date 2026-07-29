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
        // Given: a profile supported by the emulator connection catalog.

        // When: its fallback endpoints are requested.
        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints(profileName);

        // Then: its MAA-compatible first candidate is retained.
        Assert.Equal(expectedEndpoint, endpoints[0]);
    }

    [Fact]
    public void GetFallbackEndpoints_WhenProfileIsMuMu_ReturnsFiniteKnownEndpoints()
    {
        // Given: MuMu's documented local endpoint sequence.

        // When: its fallback endpoints are requested.
        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints("MuMuEmulator12");

        // Then: the resolver can probe the known instances without scanning arbitrary ports.
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
        // Given: a profile outside the supported catalog.

        // When: its fallback endpoints are requested.
        var endpoints = EmulatorProfileCatalog.GetFallbackEndpoints("Unknown");

        // Then: the caller does not probe arbitrary ports.
        Assert.Empty(endpoints);
    }
}
