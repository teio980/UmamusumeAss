using System.Text;

namespace Umamusume.CoreBridge.Tests;

public sealed class CallbackParserTests
{
    [Fact]
    public void ParseReturnsConnectionStarted()
    {
        ConnectionStartedEvent result = Assert.IsType<ConnectionStartedEvent>(
            CallbackParser.Parse(Raw(1, "ConnectionStarted", "{}")));

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
    public void NativeCallbackSizeFallbacksRemainValidForStrictV1Parser()
    {
        string escapedDiagnostics = System.Text.Json.JsonSerializer.Serialize(new
        {
            diagnostic = new string('\u0001', 12_000),
            fields = Enumerable.Repeat(new string('x', 4096), 8).ToArray(),
        });
        Assert.True(Encoding.UTF8.GetByteCount(escapedDiagnostics) > 60 * 1024);

        var failed = Assert.IsType<ConnectionFailedEvent>(CallbackParser.Parse(
            Raw(4, "ConnectionFailed", """
                {"error_code":8,"phase":"callback_serialization","message":"Connection event exceeded the protocol size limit.","attempt":1,"max_attempts":1}
                """)));
        Assert.Equal(1, failed.Attempt);
        Assert.Equal(1, failed.MaxAttempts);

        var success = Assert.IsType<ConnectionSucceededEvent>(CallbackParser.Parse(
            Raw(3, "ConnectionSucceeded", """
                {"serial":"serial","android_id":"0123456789abcdef","android_version":"14","width":1080,"height":1920,"physical_width":1080,"physical_height":1920,"size_source":"physical"}
                """)));
        Assert.Equal("serial", success.Serial);

        var progress = Assert.IsType<ConnectionProgressEvent>(CallbackParser.Parse(
            Raw(2, "ConnectionProgress", """{"phase":"adb_devices"}""")));
        Assert.Equal(ConnectionPhase.AdbDevices, progress.Phase);
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

    [Fact]
    public void ParseReturnsGameVerifiedFromVersionTwo()
    {
        var result = Assert.IsType<GameVerifiedEvent>(CallbackParser.Parse(
            RawDevice(5, "GameVerified", "{\"package_id\":\"com.example.game\"}")));

        Assert.Equal(12UL, result.Sequence);
        Assert.Equal("com.example.game", result.PackageId);
    }

    [Fact]
    public void ParseReturnsFrameMetadataAndDisplayTransform()
    {
        const string payload = """
            {"frame_id":44,"session_generation":3,"geometry_generation":8,"width":1080,"height":1920,"orientation":0,"byte_count":512,"transform":{"canonical_width":1080,"canonical_height":1920,"rotation_degrees":0,"device_width":1080,"device_height":1920,"scale":1,"offset_x":0,"offset_y":0,"content_width":1080,"content_height":1920}}
            """;

        var result = Assert.IsType<FrameCapturedEvent>(
            CallbackParser.Parse(RawDevice(7, "FrameCaptured", payload)));

        Assert.Equal(12UL, result.Sequence);
        Assert.Equal(44UL, result.Frame.FrameId);
        Assert.Equal(8UL, result.Frame.GeometryGeneration);
        Assert.Equal(512UL, result.Frame.ByteCount);
        Assert.Equal(0, result.Frame.Transform.RotationDegrees);
        Assert.Empty(result.PngBytes);
    }

    [Fact]
    public void ParseReturnsInputCoordinatesAndDeviceDisconnect()
    {
        const string payload = """
            {"source_frame":{"frame_id":44,"session_generation":3,"geometry_generation":8,"width":1080,"height":1920,"orientation":0,"byte_count":512,"transform":{"canonical_width":1080,"canonical_height":1920,"rotation_degrees":0,"device_width":1080,"device_height":1920,"scale":1,"offset_x":0,"offset_y":0,"content_width":1080,"content_height":1920}},"requested":{"first_point":{"canonical_x":150,"canonical_y":300}},"actual":{"first_point":{"device_x":150,"device_y":300}}}
            """;

        var input = Assert.IsType<InputSucceededEvent>(
            CallbackParser.Parse(RawDevice(8, "InputSucceeded", payload)));
        Assert.Equal(150, input.FirstPoint?.CanonicalX);
        Assert.Equal(300, input.FirstPoint?.DeviceY);

        var disconnected = Assert.IsType<DeviceDisconnectedEvent>(CallbackParser.Parse(
            RawDevice(10, "DeviceDisconnected", "{\"error_code\":15,\"session_generation\":42}", operationId: 0)));
        Assert.Equal(42UL, disconnected.SessionGeneration);
        Assert.Equal(ConnectionErrorCode.DeviceDisconnected, disconnected.ErrorCode);

        var reconnected = Assert.IsType<DeviceReconnectedEvent>(CallbackParser.Parse(
            RawDevice(12, "DeviceReconnected", """
                {"previous_session_generation":42,"session_generation":43,"serial":"serial","width":900,"height":1600,"physical_width":900,"physical_height":1600,"size_source":"physical"}
                """, operationId: 0)));
        Assert.Equal(42UL, reconnected.PreviousSessionGeneration);
        Assert.Equal(43UL, reconnected.SessionGeneration);
        Assert.Equal("serial", reconnected.Serial);
        Assert.Equal(900, reconnected.Width);
    }

    [Theory]
    [InlineData(10, "DeviceDisconnected", "{\"error_code\":15,\"session_generation\":42}")]
    [InlineData(12, "DeviceReconnected", "{\"previous_session_generation\":42,\"session_generation\":43,\"serial\":\"serial\",\"width\":900,\"height\":1600,\"physical_width\":900,\"physical_height\":1600,\"size_source\":\"physical\"}")]
    public void ParseRejectsSessionEventsAssociatedWithAnOperation(
        int messageId,
        string type,
        string payload)
    {
        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(RawDevice(messageId, type, payload, operationId: 7)));
    }

    [Theory]
    [InlineData(10, "DeviceDisconnected", "{\"error_code\":15}")]
    [InlineData(12, "DeviceReconnected", "{\"previous_session_generation\":42,\"serial\":\"serial\",\"width\":900,\"height\":1600,\"physical_width\":900,\"physical_height\":1600,\"size_source\":\"physical\"}")]
    public void ParseRejectsSessionEventsWithoutTheirGeneration(
        int messageId,
        string type,
        string payload)
    {
        Assert.Throws<CallbackProtocolException>(() =>
            CallbackParser.Parse(RawDevice(messageId, type, payload, operationId: 0)));
    }

    [Fact]
    public void ParseRejectsReconnectedEventsWithoutPreviousGeneration()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(RawDevice(
            12,
            "DeviceReconnected",
            "{\"session_generation\":43,\"serial\":\"serial\",\"width\":900,\"height\":1600,\"physical_width\":900,\"physical_height\":1600,\"size_source\":\"physical\"}",
            operationId: 0)));
    }

    [Fact]
    public void ParseReturnsTypedOperationFailure()
    {
        var result = Assert.IsType<DeviceOperationFailedEvent>(CallbackParser.Parse(
            RawDevice(11, "OperationFailed", "{\"error_code\":7,\"phase\":\"capture\",\"message\":\"bad png\"}")));

        Assert.Equal(ConnectionErrorCode.CommandFailed, result.ErrorCode);
        Assert.Equal("capture", result.Phase);
        Assert.Equal("bad png", result.Message);
    }

    [Fact]
    public void ParseReturnsPackageOperationWithCommandAndProcessResult()
    {
        const string payload = """
            {"operation":0,"command":{"started":true,"exit_code":0,"timed_out":false,"canceled":false,"error_code":0,"error_message":"","stdout":"Starting: Intent","stderr":""},"process_running":false}
            """;

        var result = Assert.IsType<PackageOperationCompletedEvent>(
            CallbackParser.Parse(RawDevice(13, "PackageOperationCompleted", payload)));

        Assert.Equal(PackageOperationKind.StartPackage, result.Operation);
        Assert.True(result.Command.Started);
        Assert.Equal(0, result.Command.ExitCode);
        Assert.Equal("Starting: Intent", result.Command.StandardOutput);
        Assert.False(result.ProcessRunning);
    }

    [Fact]
    public void ParseReturnsTypedGameTaskProgressAndTerminalEvents()
    {
        const string progressPayload = """
            {"task_id":"mail-collection","task_instance_id":42,"event_type":5,"task_name":"home","pipeline_name":"mail","step_index":1,"completed_units":1,"total_units":2,"succeeded":true,"score":0.99,"target_x":540,"target_y":960,"message":"clicked","timestamp_ms":1234}
            """;
        var progress = Assert.IsType<GameTaskProgressEvent>(
            CallbackParser.Parse(RawDevice(17, "GameTaskProgress", progressPayload)));

        Assert.Equal("mail-collection", progress.TaskId);
        Assert.Equal(GameTaskEventType.ActionExecuting, progress.EventType);
        Assert.Equal(1, progress.CompletedUnits);
        Assert.Equal(2, progress.TotalUnits);
        Assert.Equal(540, progress.TargetX);
        Assert.Equal(42UL, progress.TaskInstanceId);

        var completed = Assert.IsType<GameTaskCompletedEvent>(CallbackParser.Parse(
            RawDevice(18, "GameTaskCompleted", """
                {"task_id":"mail-collection","completed_units":2,"total_units":2,"process_detected":false,"error_code":0,"message":"done"}
                """)));
        Assert.Equal(ConnectionErrorCode.Success, completed.ErrorCode);
        Assert.True(completed.ProcessDetected is false);

        var failed = Assert.IsType<GameTaskFailedEvent>(CallbackParser.Parse(
            RawDevice(19, "GameTaskFailed", """
                {"task_id":"mail-collection","completed_units":1,"total_units":2,"process_detected":true,"error_code":9,"message":"canceled"}
                """)));
        Assert.Equal(ConnectionErrorCode.Canceled, failed.ErrorCode);
        Assert.True(failed.ProcessDetected);
    }

    [Fact]
    public void ParseReturnsSequencedRuntimeDiagnostic()
    {
        var diagnostic = Assert.IsType<RuntimeDiagnosticEvent>(CallbackParser.Parse(
            RawDevice(20, "RuntimeDiagnostic", """{"task_id":"mail-collection","level":"WARN","message":"skipping unavailable item"}""")));

        Assert.Equal(7UL, diagnostic.OperationId);
        Assert.Equal(12UL, diagnostic.Sequence);
        Assert.Equal("mail-collection", diagnostic.TaskId);
        Assert.Equal(RuntimeDiagnosticLevel.Warning, diagnostic.Level);
        Assert.Equal("skipping unavailable item", diagnostic.Message);
    }

    [Fact]
    public void ParseReturnsQueueEventsWithValidatedTaskInstancesAndTerminalCounts()
    {
        var started = Assert.IsType<TaskStartedEvent>(CallbackParser.Parse(RawDevice(
            21, "TaskStarted",
            """{"operation_id":7,"task_id":91,"task_instance_id":91,"name":"mail-collection","index":0,"total_tasks":2}""")));
        Assert.Equal(91UL, started.TaskId);
        Assert.Equal("mail-collection", started.Name);

        var completed = Assert.IsType<TaskCompletedEvent>(CallbackParser.Parse(RawDevice(
            24, "TaskCompleted",
            """{"operation_id":7,"task_id":91,"task_instance_id":91,"name":"mail-collection","completed_units":2,"total_units":2,"message":"done"}""")));
        Assert.Equal(91UL, completed.TaskId);
        Assert.Equal(2, completed.CompletedUnits);

        var failed = Assert.IsType<QueueFailedEvent>(CallbackParser.Parse(RawDevice(
            27, "QueueFailed",
            """{"operation_id":7,"error_code":9,"message":"canceled","succeeded_tasks":1,"failed_tasks":1,"skipped_tasks":1,"total_tasks":3}""")));
        Assert.Equal(ConnectionErrorCode.Canceled, failed.ErrorCode);
        Assert.Equal(3, failed.TotalTasks);
    }

    [Theory]
    [InlineData(21, "TaskStarted", """{"operation_id":8,"task_id":91,"task_instance_id":91,"name":"mail-collection","index":0,"total_tasks":1}""")]
    [InlineData(21, "TaskStarted", """{"operation_id":7,"task_id":91,"task_instance_id":92,"name":"mail-collection","index":1,"total_tasks":1}""")]
    [InlineData(26, "QueueCompleted", """{"succeeded_tasks":1,"failed_tasks":0,"skipped_tasks":0,"total_tasks":2}""")]
    [InlineData(25, "TaskFailed", """{"task_id":91,"task_instance_id":91,"name":"mail-collection","error_code":0,"message":"bad"}""")]
    public void ParseRejectsInconsistentQueueEvents(int messageId, string type, string payload)
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(RawDevice(messageId, type, payload)));
    }

    [Theory]
    [InlineData("TRACE")]
    [InlineData("")]
    public void ParseRejectsUnknownRuntimeDiagnosticLevel(string level)
    {
        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            task_id = "mail-collection",
            level,
            message = "diagnostic",
        });

        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            RawDevice(20, "RuntimeDiagnostic", payload)));
    }

    [Theory]
    [InlineData(12, 0, 0)]
    [InlineData(5, -1, 1)]
    [InlineData(5, 2, 1)]
    public void ParseRejectsMalformedGameTaskProgress(
        int eventType,
        int completedUnits,
        int totalUnits)
    {
        string payload = $$"""
            {"task_id":"mail-collection","event_type":{{eventType}},"task_name":"home","pipeline_name":"mail","step_index":1,"completed_units":{{completedUnits}},"total_units":{{totalUnits}},"succeeded":true,"score":0.99,"target_x":0,"target_y":0,"message":"","timestamp_ms":1}
            """;
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            RawDevice(17, "GameTaskProgress", payload)));
    }

    [Theory]
    [InlineData(18, "GameTaskCompleted", """{"task_id":"mail-collection","completed_units":1,"total_units":1,"process_detected":false,"error_code":9,"message":"bad"}""")]
    [InlineData(19, "GameTaskFailed", """{"task_id":"mail-collection","completed_units":1,"total_units":1,"process_detected":false,"error_code":0,"message":"bad"}""")]
    [InlineData(19, "GameTaskFailed", """{"task_id":"mail-collection","completed_units":2,"total_units":1,"process_detected":false,"error_code":7,"message":"bad"}""")]
    public void ParseRejectsInconsistentGameTaskTerminalPayload(int id, string type, string payload)
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            RawDevice(id, type, payload)));
    }

    [Fact]
    public void ParseRejectsUnknownPackageOperationAndErrorCode()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(RawDevice(
            13, "PackageOperationCompleted",
            """{"operation":9,"command":{"started":true,"exit_code":0,"timed_out":false,"canceled":false,"error_code":0,"error_message":"","stdout":"","stderr":""},"process_running":false}""")));
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(RawDevice(
            13, "PackageOperationCompleted",
            """{"operation":0,"command":{"started":true,"exit_code":0,"timed_out":false,"canceled":false,"error_code":99,"error_message":"","stdout":"","stderr":""},"process_running":false}""")));
    }

    [Fact]
    public void ParseRejectsVersionOneForDeviceEvents()
    {
        Assert.Throws<CallbackProtocolException>(() => CallbackParser.Parse(
            Raw(5, "GameVerified", "{\"package_id\":\"com.example.game\"}")));
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

    private static RawCallback RawDevice(int messageId, string type, string payload, ulong operationId = 7) =>
        new(messageId, $$"""{"version":2,"operation_id":{{operationId}},"sequence":12,"type":"{{type}}","payload":{{payload}}}""");
}
