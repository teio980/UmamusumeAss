using System.Text;

namespace Umamusume.CoreBridge.Tests;

public sealed class CallbackParserTests
{
    [Fact]
    public void ParseReturnsConnectionStarted()
    {
        ConnectionEvent result = CallbackParser.Parse(Raw(1, "ConnectionStarted", "{}"));

        Assert.IsType<ConnectionStartedEvent>(result);
        Assert.Equal(7UL, result.OperationId);
    }

    [Theory]
    [InlineData("adb_devices", ConnectionPhase.AdbDevices)]
    [InlineData("adb_get_state", ConnectionPhase.AdbGetState)]
    [InlineData("adb_connect", ConnectionPhase.AdbConnect)]
    [InlineData("ready_poll", ConnectionPhase.ReadyPoll)]
    [InlineData("boot_poll", ConnectionPhase.BootPoll)]
    [InlineData("android_id", ConnectionPhase.AndroidId)]
    [InlineData("android_version", ConnectionPhase.AndroidVersion)]
    [InlineData("wm_size", ConnectionPhase.WmSize)]
    public void ParseReturnsKnownConnectionProgress(string phase, ConnectionPhase expected)
    {
        var result = Assert.IsType<ConnectionProgressEvent>(
            CallbackParser.Parse(Raw(2, "ConnectionProgress", $$"""{"phase":"{{phase}}"}""")));

        Assert.Equal(expected, result.Phase);
    }

    [Fact]
    public void ParseReturnsConnectionSucceeded()
    {
        const string Payload = """
            {"serial":"127.0.0.1:5555","android_id":"0123456789abcdef","android_version":"14","width":1080,"height":1920,"physical_width":1080,"physical_height":1920,"size_source":"physical"}
            """;

        var result = Assert.IsType<ConnectionSucceededEvent>(
            CallbackParser.Parse(Raw(3, "ConnectionSucceeded", Payload)));

        Assert.Equal("127.0.0.1:5555", result.Serial);
        Assert.Equal(DisplaySizeSource.Physical, result.SizeSource);
        Assert.Equal(1080, result.Width);
        Assert.Equal(1920, result.Height);
    }

    [Fact]
    public void ParseReturnsConnectionFailed()
    {
        var result = Assert.IsType<ConnectionFailedEvent>(CallbackParser.Parse(
            Raw(4, "ConnectionFailed", """{"error_code":9,"phase":"boot_poll","message":"Canceled","attempt":1,"max_attempts":1}""")));

        Assert.Equal(ConnectionErrorCode.Canceled, result.ErrorCode);
        Assert.Equal("boot_poll", result.Phase);
        Assert.Equal("Canceled", result.Message);
        Assert.Equal(1, result.Attempt);
        Assert.Equal(1, result.MaxAttempts);
    }

    [Fact]
    public void ParseReturnsConnectionFailedWithAttemptMetadata()
    {
        var result = Assert.IsType<ConnectionFailedEvent>(CallbackParser.Parse(
            Raw(4, "ConnectionFailed", """{"error_code":9,"phase":"boot_poll","message":"Canceled","attempt":2,"max_attempts":3}""")));

        Assert.Equal(2, result.Attempt);
        Assert.Equal(3, result.MaxAttempts);
    }

    [Fact]
    public void ParseRejectsAttemptExceedingMaxAttempts()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            Raw(4, "ConnectionFailed", """{"error_code":1,"phase":"preflight","message":"fail","attempt":3,"max_attempts":1}""")));
    }

    [Fact]
    public void ParseRejectsMessageTypeMismatch()
    {
        var raw = Raw(1, "ConnectionFailed", "{}");

        var error = Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(raw));

        Assert.Equal(7UL, error.OperationId);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ParseRejectsS2Callbacks(int messageId)
    {
        var error = Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(Raw(messageId, "GameVerified", "{}")));

        Assert.Equal(DiagnosticCategory.NativeContractViolation, error.Category);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(1, "[]")]
    [InlineData(1, "not-json")]
    [InlineData(1, "{\"version\":2,\"operation_id\":7,\"type\":\"ConnectionStarted\",\"payload\":{}}")]
    [InlineData(1, "{\"version\":1,\"operation_id\":0,\"type\":\"ConnectionStarted\",\"payload\":{}}")]
    [InlineData(1, "{\"version\":1,\"operation_id\":7,\"type\":\"ConnectionStarted\"}")]
    public void ParseRejectsMalformedEnvelopes(int messageId, string json)
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(new RawCallback(messageId, json)));
    }

    [Fact]
    public void ParseRejectsNullJson()
    {
        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(new RawCallback(1, null!)));
    }

    [Fact]
    public void ParseRejectsPayloadLargerThan64Kib()
    {
        string payload = "{\"value\":\"" + new string('a', 65_537) + "\"}";

        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(Raw(1, "ConnectionStarted", payload)));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void ParseRejectsUnknownProgressPhase(string phase)
    {
        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(Raw(2, "ConnectionProgress", $$"""{"phase":"{{phase}}"}""")));
    }

    [Theory]
    [InlineData(0, 1920)]
    [InlineData(1080, -1)]
    public void ParseRejectsNonPositiveEffectiveDimensions(int width, int height)
    {
        string payload = $$"""
            {"serial":"s","android_id":"01234567","android_version":"14","width":{{width}},"height":{{height}},"physical_width":1080,"physical_height":1920,"size_source":"physical"}
            """;

        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(Raw(3, "ConnectionSucceeded", payload)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void ParseRejectsUnknownFailureCode(int errorCode)
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            Raw(4, "ConnectionFailed", $$"""{"error_code":{{errorCode}},"phase":"boot_poll","message":"failed"}""")));
    }

    [Fact]
    public void ParseRejectsMissingRequiredPayloadField()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            Raw(3, "ConnectionSucceeded", """{"serial":"s"}""")));
    }

    [Fact]
    public void ParseRejectsWrongPayloadFieldType()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            Raw(4, "ConnectionFailed", """{"error_code":"9","phase":"boot_poll","message":"failed"}""")));
    }

    private static RawCallback Raw(int messageId, string type, string payload) =>
        new(messageId, $$"""{"version":1,"operation_id":7,"type":"{{type}}","payload":{{payload}}}""");
}
