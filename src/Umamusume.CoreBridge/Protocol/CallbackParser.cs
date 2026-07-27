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
        if (raw.MessageId is >= 5 and <= 10)
        {
            throw Failure(
                DiagnosticCategory.NativeContractViolation,
                null,
                $"Callback message {raw.MessageId} is unavailable before S2.");
        }

        if (raw.MessageId is < 1 or > 4)
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
            if (version != 1)
            {
                throw Failure(DiagnosticCategory.NativeContractViolation, operationId, "Unsupported callback version.");
            }

            if (operationId is null or 0)
            {
                throw Failure(DiagnosticCategory.NativeContractViolation, operationId, "Operation ID must be nonzero.");
            }

            string type = RequiredString(root, "type", operationId);
            JsonElement payload = RequiredObject(root, "payload", operationId);
            var envelope = new CallbackEnvelope(version, operationId.Value, type, payload.Clone(), raw.MessageId);
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
        _ => throw Failure(DiagnosticCategory.UnknownEvent, envelope.OperationId, "Unknown callback message."),
    };

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

        return new ConnectionFailedEvent(
            envelope.OperationId,
            (ConnectionErrorCode)errorCode,
            RequiredString(envelope.Payload, "phase", envelope.OperationId),
            RequiredString(envelope.Payload, "message", envelope.OperationId));
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

    private static CallbackProtocolException Failure(
        DiagnosticCategory category,
        ulong? operationId,
        string message,
        Exception? innerException = null) =>
        new(category, operationId, message, innerException);
}
