namespace UmamusumeWpfGui.Models;





public sealed record GrassTaskDefinition(
    string Id,
    string NameResourceKey,
    string DescriptionResourceKey,
    string FallbackName,
    string FallbackDescription,
    bool IsEnabledByDefault = true);
