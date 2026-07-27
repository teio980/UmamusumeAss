using System;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Models;

public sealed class LastVerifiedConnectionTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new LastVerifiedConnection(
            AdbPath: @"C:\adb\adb.exe",
            Serial: "emulator-5554",
            AndroidId: "ABCDEF123456",
            AndroidVersion: "12",
            Width: 1080,
            Height: 1920,
            PhysicalWidth: 1080,
            PhysicalHeight: 1920,
            VerifiedAt: now);

        Assert.Equal(@"C:\adb\adb.exe", record.AdbPath);
        Assert.Equal("emulator-5554", record.Serial);
        Assert.Equal("ABCDEF123456", record.AndroidId);
        Assert.Equal("12", record.AndroidVersion);
        Assert.Equal(1080, record.Width);
        Assert.Equal(1920, record.Height);
        Assert.Equal(1080, record.PhysicalWidth);
        Assert.Equal(1920, record.PhysicalHeight);
        Assert.Equal(now, record.VerifiedAt);
    }

    [Fact]
    public void IsImmutableRecord_ValueSemantics()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LastVerifiedConnection("adb", "s1", "id1", "12", 100, 200, 100, 200, now);
        var b = new LastVerifiedConnection("adb", "s1", "id1", "12", 100, 200, 100, 200, now);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact]
    public void RecordsWithSameValues_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LastVerifiedConnection("adb", "s1", "id1", "12", 100, 200, 100, 200, now);
        var b = new LastVerifiedConnection("adb", "s1", "id1", "12", 100, 200, 100, 200, now);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordsWithDifferentValues_AreNotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LastVerifiedConnection("adb1", "s1", "id1", "12", 100, 200, 100, 200, now);
        var b = new LastVerifiedConnection("adb2", "s1", "id1", "12", 100, 200, 100, 200, now);

        Assert.NotEqual(a, b);
    }
}
