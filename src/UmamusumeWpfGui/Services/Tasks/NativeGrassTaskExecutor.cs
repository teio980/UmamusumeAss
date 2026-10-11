using System.Text.Json;
using System.Text.Json.Nodes;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>Freezes task settings for one Core-owned queue submission.</summary>
public sealed class NativeGrassTaskExecutor(
    IUmaService umaService,
    ISettingsService settingsService) : IGrassTaskExecutor
{
    private static readonly HashSet<string> NativeTaskIds = new(StringComparer.Ordinal)
    {
        "start-game", "mail-collection", "mission-collection",
        "shop-purchase", "daily-race", "team-race", "career-training",
    };

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IUmaService _umaService = umaService
        ?? throw new ArgumentNullException(nameof(umaService));
    private readonly ISettingsService _settingsService = settingsService
        ?? throw new ArgumentNullException(nameof(settingsService));

    public IUmaService UmaService => _umaService;

    public IReadOnlyList<GrassTaskRunRequest> Capture(IReadOnlyList<IGrassTaskModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var applicationSettings = _settingsService.Load();
        var requests = new List<GrassTaskRunRequest>(modules.Count);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            requests.Add(Capture(module, applicationSettings));
        }
        return requests.AsReadOnly();
    }

    public GrassTaskRunRequest Capture(IGrassTaskModule taskModule)
    {
        ArgumentNullException.ThrowIfNull(taskModule);
        return Capture(taskModule, _settingsService.Load());
    }

    private static GrassTaskRunRequest Capture(
        IGrassTaskModule taskModule,
        ConnectionSettings applicationSettings)
    {
        var taskId = taskModule.Definition.Id;
        if (!NativeTaskIds.Contains(taskId))
            throw new InvalidOperationException($"Core has no task route registered for '{taskId}'.");

        var settings = (JsonObject)taskModule.ExportSettings().DeepClone();
        if (settings["definitionPath"] is JsonValue definitionPathValue
            && definitionPathValue.TryGetValue<string>(out var definitionPath)
            && !string.IsNullOrWhiteSpace(definitionPath))
        {
            settings["definitionPath"] = ResourcePathRuntime.Resolve(definitionPath);
        }

        if (settings["manifestPath"] is JsonValue manifestPathValue
            && manifestPathValue.TryGetValue<string>(out var manifestPath)
            && !string.IsNullOrWhiteSpace(manifestPath))
        {
            settings["manifestPath"] = ResourcePathRuntime.Resolve(manifestPath);
        }

        if (taskId == "career-training")
        {
            settings["checkpointDirectory"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UmamusumeAss", "checkpoints");
        }
        else
        {
            settings["shop"] = JsonSerializer.SerializeToNode(
                applicationSettings.Hachimi.Shop, SnapshotJsonOptions);
        }

        if (taskId != "start-game")
        {
            settings["packageId"] = applicationSettings.TargetPackageIds.FirstOrDefault(
                package => !string.IsNullOrWhiteSpace(package))
                ?? ViewModels.Tasks.StartGameTaskSettingsViewModel.DefaultPackageId;
            settings["activityName"] = applicationSettings.TargetActivityName;
        }

        return new(taskId, settings.ToJsonString());
    }
}
