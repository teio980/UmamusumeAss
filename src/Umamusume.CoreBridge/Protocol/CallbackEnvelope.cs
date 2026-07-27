using System.Text.Json;

namespace Umamusume.CoreBridge;

internal readonly record struct CallbackEnvelope(
    int Version,
    ulong OperationId,
    string Type,
    JsonElement Payload,
    int NativeMessageId);
