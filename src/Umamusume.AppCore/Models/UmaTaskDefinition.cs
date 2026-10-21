namespace Umamusume.AppCore.Models;

public sealed record UmaTaskDefinition(
    string Id,
    string Name,
    string Description,
    bool IsEnabledByDefault = true);
