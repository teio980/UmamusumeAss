using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Loads the shared ordinary-pipeline schema strictly. Unknown properties are
/// rejected so configuration mistakes cannot silently change execution.
/// </summary>
public static class HachimiPipelineDefinitionLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<HachimiPipelineDefinition?> LoadAsync(
        string definitionPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(definitionPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(definitionPath);
            var definition = await JsonSerializer.DeserializeAsync<HachimiPipelineDefinition>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
                return null;

            if (definition.SchemaVersion != 1)
            {
                throw new JsonException(
                    $"Unsupported Hachimi pipeline schema version {definition.SchemaVersion}.");
            }

            // System.Text.Json creates a regular dictionary when deserializing
            // the JSON object. Rebuild it with MAA-like case-insensitive task
            // lookup so JSON task names and transition references cannot fail
            // merely because one side uses PascalCase and the other camelCase.
            definition.Tasks = new Dictionary<string, HachimiPipelineTask>(
                definition.Tasks,
                StringComparer.OrdinalIgnoreCase);

            definition.BaseDirectory =
                Path.GetDirectoryName(definitionPath) ?? AppContext.BaseDirectory;
            return definition;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
