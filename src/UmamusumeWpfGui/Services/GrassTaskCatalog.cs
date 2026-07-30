using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Application task registry. Future task modules can register definitions
/// here without changing the queue view or its layout.
/// </summary>
public sealed class GrassTaskCatalog : IGrassTaskCatalog
{
    private readonly List<GrassTaskDefinition> _definitions = [];

    public IReadOnlyList<GrassTaskDefinition> Definitions => _definitions;

    public void Register(GrassTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definitions.Any(item => item.Id == definition.Id))
            throw new InvalidOperationException($"Grass task '{definition.Id}' is already registered.");

        _definitions.Add(definition);
    }
}
