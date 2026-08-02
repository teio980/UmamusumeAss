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
