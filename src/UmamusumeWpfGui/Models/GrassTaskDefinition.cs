namespace UmamusumeWpfGui.Models;

/// <summary>
/// Metadata supplied by a future grass-task module.
/// The definition describes a task type; it is not a queued task instance.
/// </summary>
public sealed record GrassTaskDefinition(
    string Id,
    string NameResourceKey,
    string DescriptionResourceKey,
    string FallbackName,
    string FallbackDescription,
    bool IsEnabledByDefault = true);
