using System.Text;
using System.Text.Json;

namespace Umamusume.CoreBridge;

internal static class CallbackParser
{
    internal const int MaxCallbackJsonBytes = 65_536;

    private static readonly Dictionary<string, ConnectionPhase> Phases =
        new Dictionary<string, ConnectionPhase>(StringComparer.Ordinal)
        {
            ["adb_devices"] = ConnectionPhase.AdbDevices,
            ["adb_get_state"] = ConnectionPhase.AdbGetState,
            ["adb_connect"] = ConnectionPhase.AdbConnect,
            ["ready_poll"] = ConnectionPhase.ReadyPoll,
            ["boot_poll"] = ConnectionPhase.BootPoll,
            ["android_id"] = ConnectionPhase.AndroidId,
            ["android_version"] = ConnectionPhase.AndroidVersion,
            ["wm_size"] = ConnectionPhase.WmSize,
        };

    internal static ConnectionEvent Parse(RawCallback raw)
    {
        if (raw.MessageId is < 1 or > 27)
        {
            throw Failure(DiagnosticCategory.UnknownEvent, null, $"Unknown callback message {raw.MessageId}.");
        }

        if (string.IsNullOrEmpty(raw.Json))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, null, "Callback JSON is empty.");
        }

        if (Encoding.UTF8.GetByteCount(raw.Json) > MaxCallbackJsonBytes)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, null, "Callback JSON exceeds 65536 bytes.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                raw.Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Failure(DiagnosticCategory.MalformedCallback, null, "Callback root must be an object.");
            }

            ulong? operationId = TryReadOperationId(root);
            int version = RequiredInt32(root, "version", operationId);
            int expectedVersion = raw.MessageId <= 4 ? 1 : 2;
            if (version != expectedVersion)
            {
                throw Failure(DiagnosticCategory.NativeContractViolation, operationId, "Unsupported callback version.");
            }

            bool isSessionEvent = raw.MessageId is 10 or 12;
            if (operationId is null
                || (isSessionEvent ? operationId.Value != 0 : operationId.Value == 0))
            {
                throw Failure(
                    DiagnosticCategory.NativeContractViolation,
                    operationId,
                    isSessionEvent
                        ? "Session callbacks must use operation ID zero."
                        : "Operation callbacks must use a nonzero operation ID.");
            }

            ulong requiredOperationId = operationId.Value;
            string type = RequiredString(root, "type", requiredOperationId);
            ulong sequence = version == 2 ? RequiredPositiveUInt64(root, "sequence", requiredOperationId) : 0;
            JsonElement payload = RequiredObject(root, "payload", requiredOperationId);
            var envelope = new CallbackEnvelope(version, requiredOperationId, sequence, type, payload.Clone(), raw.MessageId);
            return ParseEnvelope(envelope);
        }
        catch (CallbackProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, null, "Callback JSON is malformed.", exception);
        }
    }

    private static ConnectionEvent ParseEnvelope(CallbackEnvelope envelope) => envelope.NativeMessageId switch
    {
        1 => ParseStarted(envelope),
        2 => ParseProgress(envelope),
        3 => ParseSucceeded(envelope),
        4 => ParseFailed(envelope),
        5 => ParseGameVerified(envelope),
        6 => ParseGameVerificationFailed(envelope),
        7 => ParseFrameCaptured(envelope),
        8 => ParseInputSucceeded(envelope),
        9 => ParseInputFailed(envelope),
        10 => ParseDeviceDisconnected(envelope),
        11 => ParseOperationFailed(envelope),
        12 => ParseDeviceReconnected(envelope),
        13 => ParsePackageOperationCompleted(envelope),
        14 => ParseTaskProgress(envelope),
        15 => ParsePipelineCompleted(envelope),
        16 => ParsePipelineFailed(envelope),
        17 => ParseGameTaskProgress(envelope),
        18 => ParseGameTaskCompleted(envelope),
        19 => ParseGameTaskFailed(envelope),
        20 => ParseRuntimeDiagnostic(envelope),
        21 => ParseTaskStarted(envelope),
        22 => ParseTaskDecision(envelope),
        23 => ParseTaskActionConfirmed(envelope),
        24 => ParseTaskCompleted(envelope),
        25 => ParseTaskFailed(envelope),
        26 => ParseQueueCompleted(envelope),
        27 => ParseQueueFailed(envelope),
        _ => throw Failure(DiagnosticCategory.UnknownEvent, envelope.OperationId, "Unknown callback message."),
    };

    private static GameVerifiedEvent ParseGameVerified(CallbackEnvelope envelope)
    {
        RequireType(envelope, "GameVerified");
        return new GameVerifiedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredString(envelope.Payload, "package_id", envelope.OperationId));
    }

    private static GameVerificationFailedEvent ParseGameVerificationFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "GameVerificationFailed");
        return new GameVerificationFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredErrorCode(envelope.Payload, envelope.OperationId),
            RequiredString(envelope.Payload, "phase", envelope.OperationId),
            RequiredString(envelope.Payload, "message", envelope.OperationId));
    }

    private static FrameCapturedEvent ParseFrameCaptured(CallbackEnvelope envelope)
    {
        RequireType(envelope, "FrameCaptured");
        DeviceFrameMetadata frame = ParseFrame(envelope.Payload, envelope.OperationId);
        return new FrameCapturedEvent(envelope.OperationId, envelope.Sequence, frame, []);
    }

    private static InputSucceededEvent ParseInputSucceeded(CallbackEnvelope envelope)
    {
        RequireType(envelope, "InputSucceeded");
        JsonElement payload = envelope.Payload;
        DeviceFrameMetadata frame = ParseFrame(
            RequiredObject(payload, "source_frame", envelope.OperationId), envelope.OperationId);
        return new InputSucceededEvent(
            envelope.OperationId,
            envelope.Sequence,
            frame,
            ParseInputPoint(payload, "first_point", envelope.OperationId),
            ParseInputPoint(payload, "second_point", envelope.OperationId));
    }

    private static InputFailedEvent ParseInputFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "InputFailed");
        JsonElement payload = envelope.Payload;
        DeviceFrameMetadata? frame = payload.TryGetProperty("source_frame", out JsonElement frameElement)
            ? ParseFrame(frameElement, envelope.OperationId)
            : null;
        return new InputFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredErrorCode(payload, envelope.OperationId),
            RequiredString(payload, "phase", envelope.OperationId),
            RequiredString(payload, "message", envelope.OperationId),
            frame,
            ParseInputPoint(payload, "first_point", envelope.OperationId),
            ParseInputPoint(payload, "second_point", envelope.OperationId));
    }

    private static DeviceDisconnectedEvent ParseDeviceDisconnected(CallbackEnvelope envelope)
    {
        RequireType(envelope, "DeviceDisconnected");
        int code = RequiredInt32(envelope.Payload, "error_code", envelope.OperationId);
        if (code != (int)ConnectionErrorCode.DeviceDisconnected)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown disconnect error code.");
        return new DeviceDisconnectedEvent(
            RequiredPositiveUInt64(envelope.Payload, "session_generation", envelope.OperationId),
            envelope.Sequence,
            (ConnectionErrorCode)code);
    }

    private static DeviceReconnectedEvent ParseDeviceReconnected(CallbackEnvelope envelope)
    {
        RequireType(envelope, "DeviceReconnected");
        JsonElement payload = envelope.Payload;
        ulong previousGeneration = RequiredPositiveUInt64(
            payload, "previous_session_generation", envelope.OperationId);
        ulong sessionGeneration = RequiredPositiveUInt64(
            payload, "session_generation", envelope.OperationId);
        if (sessionGeneration <= previousGeneration)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "A reconnected session generation must increase.");
        string source = RequiredString(payload, "size_source", envelope.OperationId);
        DisplaySizeSource sizeSource = source switch
        {
            "physical" => DisplaySizeSource.Physical,
            "override" => DisplaySizeSource.Override,
            _ => throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown display size source."),
        };
        return new DeviceReconnectedEvent(
            previousGeneration,
            sessionGeneration,
            envelope.Sequence,
            RequiredString(payload, "serial", envelope.OperationId),
            RequiredPositiveInt32(payload, "width", envelope.OperationId),
            RequiredPositiveInt32(payload, "height", envelope.OperationId),
            RequiredPositiveInt32(payload, "physical_width", envelope.OperationId),
            RequiredPositiveInt32(payload, "physical_height", envelope.OperationId),
            sizeSource);
    }

    private static DeviceOperationFailedEvent ParseOperationFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "OperationFailed");
        return new DeviceOperationFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredErrorCode(envelope.Payload, envelope.OperationId),
            RequiredString(envelope.Payload, "phase", envelope.OperationId),
            RequiredString(envelope.Payload, "message", envelope.OperationId));
    }

    private static PackageOperationCompletedEvent ParsePackageOperationCompleted(
        CallbackEnvelope envelope)
    {
        RequireType(envelope, "PackageOperationCompleted");
        JsonElement payload = envelope.Payload;
        int operationValue = RequiredInt32(payload, "operation", envelope.OperationId);
        if (!Enum.IsDefined(typeof(PackageOperationKind), operationValue))
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Unknown package operation.");
        JsonElement command = RequiredObject(payload, "command", envelope.OperationId);
        int errorCode = RequiredInt32(command, "error_code", envelope.OperationId);
        if (errorCode is < 0 or > 17)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Unknown package command error code.");
        var commandResult = new PackageCommandResult(
            RequiredBoolean(command, "started", envelope.OperationId),
            RequiredInt32(command, "exit_code", envelope.OperationId),
            RequiredBoolean(command, "timed_out", envelope.OperationId),
            RequiredBoolean(command, "canceled", envelope.OperationId),
            (ConnectionErrorCode)errorCode,
            RequiredStringAllowEmpty(command, "error_message", envelope.OperationId),
            RequiredStringAllowEmpty(command, "stdout", envelope.OperationId),
            RequiredStringAllowEmpty(command, "stderr", envelope.OperationId));
        return new PackageOperationCompletedEvent(
            envelope.OperationId,
            envelope.Sequence,
            (PackageOperationKind)operationValue,
            commandResult,
            RequiredBoolean(payload, "process_running", envelope.OperationId));
    }

    private static TaskProgressEvent ParseTaskProgress(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskProgress");
        JsonElement p = envelope.Payload;
        return new TaskProgressEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredInt32(p, "event_type", envelope.OperationId),
            RequiredString(p, "task_name", envelope.OperationId),
            RequiredString(p, "pipeline_name", envelope.OperationId),
            RequiredInt32(p, "step_index", envelope.OperationId),
            p.TryGetProperty("succeeded", out var succ) && succ.GetBoolean(),
            p.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0.0,
            p.TryGetProperty("target_x", out var tx) ? tx.GetInt32() : 0,
            p.TryGetProperty("target_y", out var ty) ? ty.GetInt32() : 0,
            p.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "",
            p.TryGetProperty("timestamp_ms", out var ts) ? ts.GetInt64() : 0L);
    }

    private static PipelineCompletedEvent ParsePipelineCompleted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "PipelineCompleted");
        JsonElement p = envelope.Payload;
        return new PipelineCompletedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredInt32(p, "status", envelope.OperationId),
            p.TryGetProperty("final_task", out var ft) ? ft.GetString() ?? "" : "",
            p.TryGetProperty("total_steps", out var ts) ? ts.GetInt32() : 0);
    }

    private static PipelineFailedEvent ParsePipelineFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "PipelineFailed");
        JsonElement p = envelope.Payload;
        return new PipelineFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            p.TryGetProperty("status", out var st) ? st.GetInt32() : -1,
            p.TryGetProperty("final_task", out var ft) ? ft.GetString() ?? "" : "",
            p.TryGetProperty("total_steps", out var ts) ? ts.GetInt32() : 0,
            p.TryGetProperty("error_message", out var em) ? em.GetString() ?? "" : "");
    }

    private static GameTaskProgressEvent ParseGameTaskProgress(CallbackEnvelope envelope)
    {
        RequireType(envelope, "GameTaskProgress");
        JsonElement payload = envelope.Payload;
        int eventType = RequiredInt32(payload, "event_type", envelope.OperationId);
        if (!Enum.IsDefined(typeof(GameTaskEventType), eventType))
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown game-task event type.");

        int completedUnits = RequiredNonNegativeInt32(payload, "completed_units", envelope.OperationId);
        int totalUnits = RequiredNonNegativeInt32(payload, "total_units", envelope.OperationId);
        if (totalUnits > 0 && completedUnits > totalUnits)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Game-task completed units exceed total units.");

        return new GameTaskProgressEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredString(payload, "task_id", envelope.OperationId),
            (GameTaskEventType)eventType,
            RequiredStringAllowEmpty(payload, "task_name", envelope.OperationId),
            RequiredStringAllowEmpty(payload, "pipeline_name", envelope.OperationId),
            RequiredNonNegativeInt32(payload, "step_index", envelope.OperationId),
            completedUnits,
            totalUnits,
            RequiredBoolean(payload, "succeeded", envelope.OperationId),
            RequiredDouble(payload, "score", envelope.OperationId),
            RequiredInt32(payload, "target_x", envelope.OperationId),
            RequiredInt32(payload, "target_y", envelope.OperationId),
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId),
            RequiredInt64(payload, "timestamp_ms", envelope.OperationId),
            OptionalUInt64AllowZero(payload, "task_instance_id", envelope.OperationId));
    }

    private static GameTaskCompletedEvent ParseGameTaskCompleted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "GameTaskCompleted");
        JsonElement payload = envelope.Payload;
        int completedUnits = RequiredNonNegativeInt32(payload, "completed_units", envelope.OperationId);
        int totalUnits = RequiredNonNegativeInt32(payload, "total_units", envelope.OperationId);
        if ((totalUnits > 0 && completedUnits > totalUnits)
            || RequiredInt32(payload, "error_code", envelope.OperationId) != 0)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Successful game-task terminal payload is inconsistent.");

        return new GameTaskCompletedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredString(payload, "task_id", envelope.OperationId),
            completedUnits,
            totalUnits,
            RequiredBoolean(payload, "process_detected", envelope.OperationId),
            ConnectionErrorCode.Success,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId));
    }

    private static GameTaskFailedEvent ParseGameTaskFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "GameTaskFailed");
        JsonElement payload = envelope.Payload;
        int completedUnits = RequiredNonNegativeInt32(payload, "completed_units", envelope.OperationId);
        int totalUnits = RequiredNonNegativeInt32(payload, "total_units", envelope.OperationId);
        int errorCode = RequiredInt32(payload, "error_code", envelope.OperationId);
        if ((totalUnits > 0 && completedUnits > totalUnits)
            || !Enum.IsDefined(typeof(ConnectionErrorCode), errorCode)
            || errorCode == (int)ConnectionErrorCode.Success)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Failed game-task terminal payload is inconsistent.");

        return new GameTaskFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredString(payload, "task_id", envelope.OperationId),
            completedUnits,
            totalUnits,
            RequiredBoolean(payload, "process_detected", envelope.OperationId),
            (ConnectionErrorCode)errorCode,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId));
    }

    private static RuntimeDiagnosticEvent ParseRuntimeDiagnostic(CallbackEnvelope envelope)
    {
        RequireType(envelope, "RuntimeDiagnostic");
        JsonElement payload = envelope.Payload;
        string level = RequiredString(payload, "level", envelope.OperationId);
        RuntimeDiagnosticLevel parsedLevel = level switch
        {
            "DEBUG" => RuntimeDiagnosticLevel.Debug,
            "INFO" => RuntimeDiagnosticLevel.Information,
            "WARN" => RuntimeDiagnosticLevel.Warning,
            "ERROR" => RuntimeDiagnosticLevel.Error,
            _ => throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Unknown runtime diagnostic level."),
        };

        return new RuntimeDiagnosticEvent(
            envelope.OperationId,
            envelope.Sequence,
            RequiredStringAllowEmpty(payload, "task_id", envelope.OperationId),
            parsedLevel,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId),
            OptionalUInt64AllowZero(payload, "task_instance_id", envelope.OperationId));
    }

    private static TaskStartedEvent ParseTaskStarted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskStarted");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        ulong taskId = RequiredPositiveUInt64(payload, "task_id", envelope.OperationId);
        RequireMatchingTaskInstanceId(payload, taskId, envelope.OperationId);
        int index = RequiredNonNegativeInt32(payload, "index", envelope.OperationId);
        int totalTasks = RequiredPositiveInt32(payload, "total_tasks", envelope.OperationId);
        if (index >= totalTasks)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "TaskStarted index must be less than total_tasks.");
        return new TaskStartedEvent(
            envelope.OperationId,
            envelope.Sequence,
            taskId,
            RequiredString(payload, "name", envelope.OperationId),
            index,
            totalTasks);
    }

    private static TaskDecisionEvent ParseTaskDecision(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskDecision");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        ulong taskId = RequiredPositiveUInt64(payload, "task_id", envelope.OperationId);
        RequireMatchingTaskInstanceId(payload, taskId, envelope.OperationId);
        return new TaskDecisionEvent(
            envelope.OperationId,
            envelope.Sequence,
            taskId,
            RequiredString(payload, "decision_type", envelope.OperationId),
            RequiredStringAllowEmpty(payload, "details", envelope.OperationId));
    }

    private static TaskActionConfirmedEvent ParseTaskActionConfirmed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskActionConfirmed");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        ulong taskId = RequiredPositiveUInt64(payload, "task_id", envelope.OperationId);
        RequireMatchingTaskInstanceId(payload, taskId, envelope.OperationId);
        return new TaskActionConfirmedEvent(
            envelope.OperationId,
            envelope.Sequence,
            taskId,
            RequiredString(payload, "action_name", envelope.OperationId),
            RequiredBoolean(payload, "confirmed", envelope.OperationId));
    }

    private static TaskCompletedEvent ParseTaskCompleted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskCompleted");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        ulong taskId = RequiredPositiveUInt64(payload, "task_id", envelope.OperationId);
        RequireMatchingTaskInstanceId(payload, taskId, envelope.OperationId);
        int completedUnits = RequiredNonNegativeInt32(payload, "completed_units", envelope.OperationId);
        int totalUnits = RequiredNonNegativeInt32(payload, "total_units", envelope.OperationId);
        if (totalUnits > 0 && completedUnits > totalUnits)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "Queue task completed units exceed total units.");
        return new TaskCompletedEvent(
            envelope.OperationId,
            envelope.Sequence,
            taskId,
            RequiredString(payload, "name", envelope.OperationId),
            completedUnits,
            totalUnits,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId));
    }

    private static TaskFailedEvent ParseTaskFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "TaskFailed");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        int errorCode = RequiredInt32(payload, "error_code", envelope.OperationId);
        if (errorCode == (int)ConnectionErrorCode.Success)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "TaskFailed must carry a failure error code.");
        ulong taskId = RequiredPositiveUInt64(payload, "task_id", envelope.OperationId);
        RequireMatchingTaskInstanceId(payload, taskId, envelope.OperationId);
        return new TaskFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            taskId,
            RequiredString(payload, "name", envelope.OperationId),
            Enum.IsDefined(typeof(ConnectionErrorCode), errorCode)
                ? (ConnectionErrorCode)errorCode
                : ConnectionErrorCode.CommandFailed,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId));
    }

    private static QueueCompletedEvent ParseQueueCompleted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "QueueCompleted");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        int succeeded = RequiredNonNegativeInt32(payload, "succeeded_tasks", envelope.OperationId);
        int failed = RequiredNonNegativeInt32(payload, "failed_tasks", envelope.OperationId);
        int skipped = RequiredNonNegativeInt32(payload, "skipped_tasks", envelope.OperationId);
        int total = RequiredNonNegativeInt32(payload, "total_tasks", envelope.OperationId);
        ValidateQueueCounts(envelope.OperationId, succeeded, failed, skipped, total);
        return new QueueCompletedEvent(
            envelope.OperationId,
            envelope.Sequence,
            succeeded,
            failed,
            skipped,
            total);
    }

    private static QueueFailedEvent ParseQueueFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "QueueFailed");
        JsonElement payload = envelope.Payload;
        RequireMatchingPayloadOperationId(payload, envelope.OperationId);
        int errorCode = RequiredInt32(payload, "error_code", envelope.OperationId);
        if (errorCode == (int)ConnectionErrorCode.Success)
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId,
                "QueueFailed must carry a failure error code.");
        int succeeded = RequiredNonNegativeInt32(payload, "succeeded_tasks", envelope.OperationId);
        int failed = RequiredNonNegativeInt32(payload, "failed_tasks", envelope.OperationId);
        int skipped = RequiredNonNegativeInt32(payload, "skipped_tasks", envelope.OperationId);
        int total = RequiredNonNegativeInt32(payload, "total_tasks", envelope.OperationId);
        ValidateQueueCounts(envelope.OperationId, succeeded, failed, skipped, total);
        return new QueueFailedEvent(
            envelope.OperationId,
            envelope.Sequence,
            Enum.IsDefined(typeof(ConnectionErrorCode), errorCode)
                ? (ConnectionErrorCode)errorCode
                : ConnectionErrorCode.CommandFailed,
            RequiredStringAllowEmpty(payload, "message", envelope.OperationId),
            succeeded,
            failed,
            skipped,
            total);
    }

    private static void ValidateQueueCounts(
        ulong operationId,
        int succeeded,
        int failed,
        int skipped,
        int total)
    {
        if ((long)succeeded + failed + skipped != total)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId,
                "Queue task counts must add up to total_tasks.");
    }

    private static void RequireMatchingPayloadOperationId(JsonElement payload, ulong envelopeOperationId)
    {
        if (!payload.TryGetProperty("operation_id", out JsonElement value))
            return;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetUInt64(out ulong payloadOperationId)
            || payloadOperationId != envelopeOperationId)
            throw Failure(DiagnosticCategory.NativeContractViolation, envelopeOperationId,
                "The callback payload operation_id does not match its envelope.");
    }

    private static void RequireMatchingTaskInstanceId(JsonElement payload, ulong taskId, ulong operationId)
    {
        if (!payload.TryGetProperty("task_instance_id", out JsonElement value))
            return;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetUInt64(out ulong taskInstanceId)
            || taskInstanceId != taskId)
            throw Failure(DiagnosticCategory.NativeContractViolation, operationId,
                "The queue task ID and task_instance_id do not match.");
    }

    private static DeviceFrameMetadata ParseFrame(JsonElement parent, ulong operationId)
    {
        int orientation = RequiredInt32(parent, "orientation", operationId);
        if (orientation is < 0 or > 3)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, "Unknown display orientation.");
        JsonElement transformElement = RequiredObject(parent, "transform", operationId);
        int rotation = RequiredInt32(transformElement, "rotation_degrees", operationId);
        if (rotation != orientation * 90)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, "Display rotation does not match orientation.");
        var transform = new DeviceDisplayTransform(
            RequiredPositiveInt32(transformElement, "canonical_width", operationId),
            RequiredPositiveInt32(transformElement, "canonical_height", operationId),
            rotation,
            RequiredPositiveInt32(transformElement, "device_width", operationId),
            RequiredPositiveInt32(transformElement, "device_height", operationId),
            RequiredPositiveDouble(transformElement, "scale", operationId),
            RequiredDouble(transformElement, "offset_x", operationId),
            RequiredDouble(transformElement, "offset_y", operationId),
            RequiredPositiveDouble(transformElement, "content_width", operationId),
            RequiredPositiveDouble(transformElement, "content_height", operationId));
        int width = RequiredPositiveInt32(parent, "width", operationId);
        int height = RequiredPositiveInt32(parent, "height", operationId);
        if (transform.DeviceWidth != width || transform.DeviceHeight != height)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, "Frame size does not match its display transform.");
        ulong byteCount = RequiredPositiveUInt64(parent, "byte_count", operationId);
        return new DeviceFrameMetadata(
            RequiredPositiveUInt64(parent, "frame_id", operationId),
            RequiredPositiveUInt64(parent, "session_generation", operationId),
            RequiredPositiveUInt64(parent, "geometry_generation", operationId),
            width, height, orientation, byteCount, transform);
    }

    private static DeviceInputPoint? ParseInputPoint(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty("requested", out JsonElement requested)
            || requested.ValueKind != JsonValueKind.Object
            || !requested.TryGetProperty(name, out JsonElement request))
            return null;
        int canonicalX = RequiredInt32(request, "canonical_x", operationId);
        int canonicalY = RequiredInt32(request, "canonical_y", operationId);
        int? deviceX = null;
        int? deviceY = null;
        if (parent.TryGetProperty("actual", out JsonElement actual)
            && actual.ValueKind == JsonValueKind.Object
            && actual.TryGetProperty(name, out JsonElement actualPoint))
        {
            deviceX = RequiredInt32(actualPoint, "device_x", operationId);
            deviceY = RequiredInt32(actualPoint, "device_y", operationId);
        }
        return new DeviceInputPoint(canonicalX, canonicalY, deviceX, deviceY);
    }

    private static ConnectionErrorCode RequiredErrorCode(JsonElement parent, ulong operationId)
    {
        int value = RequiredInt32(parent, "error_code", operationId);
        if (value is < 1 or > 15)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, "Unknown device operation error code.");
        return (ConnectionErrorCode)value;
    }

    private static ConnectionStartedEvent ParseStarted(CallbackEnvelope envelope)
    {
        RequireType(envelope, "ConnectionStarted");
        if (envelope.Payload.EnumerateObject().Any())
        {
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "ConnectionStarted payload must be empty.");
        }

        return new ConnectionStartedEvent(envelope.OperationId);
    }

    private static ConnectionProgressEvent ParseProgress(CallbackEnvelope envelope)
    {
        RequireType(envelope, "ConnectionProgress");
        string phase = RequiredString(envelope.Payload, "phase", envelope.OperationId);
        if (!Phases.TryGetValue(phase, out ConnectionPhase parsedPhase))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown connection phase.");
        }

        return new ConnectionProgressEvent(envelope.OperationId, parsedPhase);
    }

    private static ConnectionSucceededEvent ParseSucceeded(CallbackEnvelope envelope)
    {
        RequireType(envelope, "ConnectionSucceeded");
        string source = RequiredString(envelope.Payload, "size_source", envelope.OperationId);
        DisplaySizeSource sizeSource = source switch
        {
            "physical" => DisplaySizeSource.Physical,
            "override" => DisplaySizeSource.Override,
            _ => throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown display size source."),
        };

        return new ConnectionSucceededEvent(
            envelope.OperationId,
            RequiredString(envelope.Payload, "serial", envelope.OperationId),
            RequiredString(envelope.Payload, "android_id", envelope.OperationId),
            RequiredString(envelope.Payload, "android_version", envelope.OperationId),
            RequiredPositiveInt32(envelope.Payload, "width", envelope.OperationId),
            RequiredPositiveInt32(envelope.Payload, "height", envelope.OperationId),
            RequiredPositiveInt32(envelope.Payload, "physical_width", envelope.OperationId),
            RequiredPositiveInt32(envelope.Payload, "physical_height", envelope.OperationId),
            sizeSource);
    }

    private static ConnectionFailedEvent ParseFailed(CallbackEnvelope envelope)
    {
        RequireType(envelope, "ConnectionFailed");
        int errorCode = RequiredInt32(envelope.Payload, "error_code", envelope.OperationId);
        if (errorCode is < 1 or > 15)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "Unknown connection error code.");
        }

        int attempt = RequiredPositiveInt32(envelope.Payload, "attempt", envelope.OperationId);
        int maxAttempts = RequiredPositiveInt32(envelope.Payload, "max_attempts", envelope.OperationId);
        if (attempt > maxAttempts)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, envelope.OperationId, "attempt must not exceed max_attempts.");
        }

        return new ConnectionFailedEvent(
            envelope.OperationId,
            (ConnectionErrorCode)errorCode,
            RequiredString(envelope.Payload, "phase", envelope.OperationId),
            RequiredString(envelope.Payload, "message", envelope.OperationId),
            attempt,
            maxAttempts);
    }

    private static void RequireType(CallbackEnvelope envelope, string expected)
    {
        if (!string.Equals(envelope.Type, expected, StringComparison.Ordinal))
        {
            throw Failure(
                DiagnosticCategory.NativeContractViolation,
                envelope.OperationId,
                "Native message ID and callback type do not match.");
        }
    }

    private static ulong? TryReadOperationId(JsonElement root)
    {
        return root.TryGetProperty("operation_id", out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt64(out ulong value)
            ? value
            : null;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, ulong? operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.Object)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required object '{name}' is missing.");
        }

        return property;
    }

    private static string RequiredString(JsonElement parent, string name, ulong? operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required string '{name}' is missing.");
        }

        string? value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required string '{name}' is empty.");
        }

        return value;
    }

    private static string RequiredStringAllowEmpty(JsonElement parent, string name, ulong? operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId,
                $"Required string '{name}' is missing.");
        return property.GetString() ?? string.Empty;
    }

    private static bool RequiredBoolean(JsonElement parent, string name, ulong? operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure(DiagnosticCategory.MalformedCallback, operationId,
                $"Required boolean '{name}' is missing.");
        return property.GetBoolean();
    }

    private static int RequiredInt32(JsonElement parent, string name, ulong? operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int value))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required integer '{name}' is missing.");
        }

        return value;
    }

    private static int RequiredPositiveInt32(JsonElement parent, string name, ulong operationId)
    {
        int value = RequiredInt32(parent, name, operationId);
        if (value <= 0)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"'{name}' must be positive.");
        }

        return value;
    }

    private static int RequiredNonNegativeInt32(JsonElement parent, string name, ulong operationId)
    {
        int value = RequiredInt32(parent, name, operationId);
        if (value < 0)
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"'{name}' must not be negative.");
        return value;
    }

    private static long RequiredInt64(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long value))
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required integer '{name}' is missing.");
        return value;
    }

    private static ulong RequiredPositiveUInt64(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetUInt64(out ulong value)
            || value == 0)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required positive integer '{name}' is missing.");
        }

        return value;
    }

    private static ulong RequiredUInt64(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetUInt64(out ulong value))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required integer '{name}' is missing.");
        }

        return value;
    }

    private static ulong OptionalUInt64AllowZero(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property))
            return 0;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out ulong value))
            throw Failure(DiagnosticCategory.MalformedCallback, operationId,
                $"Optional integer '{name}' must be an unsigned 64-bit value.");
        return value;
    }

    private static double RequiredPositiveDouble(JsonElement parent, string name, ulong operationId)
    {
        double value = RequiredDouble(parent, name, operationId);
        if (value <= 0)
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"'{name}' must be positive.");
        }

        return value;
    }

    private static double RequiredDouble(JsonElement parent, string name, ulong operationId)
    {
        if (!parent.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out double value)
            || !double.IsFinite(value))
        {
            throw Failure(DiagnosticCategory.MalformedCallback, operationId, $"Required finite number '{name}' is missing.");
        }

        return value;
    }

    private static CallbackProtocolException Failure(
        DiagnosticCategory category,
        ulong? operationId,
        string message,
        Exception? innerException = null) =>
        new(category, operationId, message, innerException);
}
