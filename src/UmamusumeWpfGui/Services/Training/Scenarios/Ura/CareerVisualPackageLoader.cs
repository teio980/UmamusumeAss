using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed record CareerVisualPackageContents(
    string ManifestPath,
    string ScenarioRoot,
    string SharedRoot,
    IReadOnlyList<string> ProfileFragmentPaths,
    IReadOnlyList<string> ExecutionFragmentPaths,
    string ResourceCatalogPath,
    UraScreenProfile ScreenProfile,
    HachimiPipelineDefinition ExecutionDefinition,
    CareerVisualResourcePackage Resources);

/// <summary>Loads and composes v2 Career screen and execution fragments.</summary>
public static class CareerVisualPackageLoader
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<CareerVisualPackageContents> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedManifest = Path.GetFullPath(
            Path.IsPathRooted(manifestPath) ? manifestPath : ResourcePathRuntime.Resolve(manifestPath));
        var scenarioRoot = Path.GetDirectoryName(resolvedManifest)
            ?? throw new InvalidDataException("Career manifest has no parent directory.");
        var sharedRoot = Directory.GetParent(scenarioRoot)?.FullName
            ?? throw new InvalidDataException("Career manifest is outside a shared resource root.");
        var manifest = await ReadCareerManifestAsync(resolvedManifest, cancellationToken)
            .ConfigureAwait(false);
        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidDataException(
                $"Career manifest schemaVersion {manifest.SchemaVersion} is legacy or unsupported. "
                + "Migrate it to schemaVersion 2 with ordered 'screens' and 'execution' fragment arrays "
                + "and source-relative resource paths. Ordinary pipeline JSON remains schemaVersion 1.");
        }
        if (manifest.Screens.Count == 0 || manifest.Execution.Count == 0)
            throw new InvalidDataException("Career schemaVersion 2 requires non-empty screens and execution fragment arrays.");

        var resourcePackage = new CareerVisualResourcePackage(sharedRoot, scenarioRoot);
        var catalogPath = ResolvePackageFile(
            scenarioRoot,
            sharedRoot,
            manifest.ResourceCatalog,
            "resource catalog");
        await LoadCatalogAsync(
                catalogPath,
                scenarioRoot,
                sharedRoot,
                resourcePackage,
                cancellationToken)
            .ConfigureAwait(false);

        var profilePaths = manifest.Screens
            .Select(path => ResolvePackageFile(scenarioRoot, sharedRoot, path, "screen fragment"))
            .ToArray();
        var executionPaths = manifest.Execution
            .Select(path => ResolvePackageFile(scenarioRoot, sharedRoot, path, "execution fragment"))
            .ToArray();
        var profile = await LoadProfileFragmentsAsync(
                profilePaths,
                manifest.ScenarioId,
                resourcePackage,
                cancellationToken)
            .ConfigureAwait(false);
        var execution = await LoadExecutionFragmentsAsync(
                executionPaths,
                profile,
                resourcePackage,
                sharedRoot,
                scenarioRoot,
                cancellationToken)
            .ConfigureAwait(false);

        profile.VisualResources = resourcePackage;
        ValidateScenarioSelection(profile, resourcePackage);
        ValidateClawMachine(profile, resourcePackage);
        ValidateComposedPackage(profile, execution, resourcePackage);
        return new CareerVisualPackageContents(
            resolvedManifest,
            scenarioRoot,
            sharedRoot,
            profilePaths,
            executionPaths,
            catalogPath,
            profile,
            execution,
            resourcePackage);
    }

    public static async Task<UraScreenProfile> LoadScreenProfileAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var contents = await LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return contents.ScreenProfile;
    }

    public static async Task<HachimiPipelineDefinition> LoadExecutionDefinitionAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var contents = await LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return contents.ExecutionDefinition;
    }

    private static async Task<UraScenarioManifest> ReadCareerManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Career manifest root must be an object.");

        var root = document.RootElement;
        var version = root.TryGetProperty("schemaVersion", out var versionElement)
            && versionElement.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : 1;
        if (version == 1)
        {
            throw new InvalidDataException(
                "Legacy Career manifest schemaVersion 1 is no longer supported. "
                + "Migrate it to schemaVersion 2 with ordered 'screens' and 'execution' fragment arrays; "
                + "ordinary pipeline schemaVersion 1 remains supported.");
        }
        foreach (var propertyName in new[] { "screens", "execution" })
        {
            if (!root.TryGetProperty(propertyName, out var field)
                || field.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Career manifest '{propertyName}' must be an ordered array of fragment paths.");
            }
        }

        return JsonSerializer.Deserialize<UraScenarioManifest>(root.GetRawText(), StrictJsonOptions)
            ?? throw new InvalidDataException("Career manifest is empty.");
    }

    private static async Task<UraScreenProfile> LoadProfileFragmentsAsync(
        IReadOnlyList<string> fragmentPaths,
        string scenarioId,
        CareerVisualResourcePackage resources,
        CancellationToken cancellationToken)
    {
        UraScreenProfile? merged = null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragmentPath in fragmentPaths)
        {
            var fragment = await ReadStrictAsync<UraScreenProfile>(fragmentPath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Career screen fragment '{fragmentPath}' is empty.");
            if (fragment.Schema is not ("uma.screen.profile.fragment.v1" or "uma.screen.profile.v1"))
                throw new InvalidDataException($"Career screen fragment '{fragmentPath}' has an unsupported $schema.");
            if (!string.Equals(fragment.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Career screen fragment '{fragmentPath}' has a mismatched scenarioId.");
            if (fragment.ReferenceWidth <= 0 || fragment.ReferenceHeight <= 0)
                throw new InvalidDataException($"Career screen fragment '{fragmentPath}' has invalid reference dimensions.");

            merged ??= new UraScreenProfile
            {
                ProfileId = fragment.ProfileId,
                ScenarioId = fragment.ScenarioId,
                ReferenceWidth = fragment.ReferenceWidth,
                ReferenceHeight = fragment.ReferenceHeight,
                ScenarioSelection = fragment.ScenarioSelection,
                ScenarioSelectionSourceDirectory = fragment.ScenarioSelection is null
                    ? null
                    : Path.GetDirectoryName(fragmentPath),
                ClawMachine = fragment.ClawMachine,
                ClawMachineSourceDirectory = Path.GetDirectoryName(fragmentPath),
            };
            if (fragment.ReferenceWidth != merged.ReferenceWidth
                || fragment.ReferenceHeight != merged.ReferenceHeight)
            {
                throw new InvalidDataException(
                    $"Career screen fragment '{fragmentPath}' uses a different reference resolution.");
            }

            if (merged.ScenarioSelection is null && fragment.ScenarioSelection is not null)
            {
                merged.ScenarioSelection = fragment.ScenarioSelection;
                merged.ScenarioSelectionSourceDirectory = Path.GetDirectoryName(fragmentPath);
            }

            if (ReferenceEquals(merged.ClawMachine, fragment.ClawMachine)
                && !string.IsNullOrWhiteSpace(fragment.ClawMachine.CreditLabelTemplate))
            {
                var originDirectory = merged.ClawMachineSourceDirectory
                    ?? Path.GetDirectoryName(fragmentPath)!;
                var creditLabel = resources.ResolveFromOrigin(
                    originDirectory,
                    fragment.ClawMachine.CreditLabelTemplate,
                    "claw-machine credit label");
                resources.AddSourceAlias(fragment.ClawMachine.CreditLabelTemplate, creditLabel);
            }

            foreach (var screen in fragment.Screens)
            {
                if (string.IsNullOrWhiteSpace(screen.ScreenId) || !ids.Add(screen.ScreenId))
                    throw new InvalidDataException(
                        $"Career screen fragments contain a missing or duplicate screen ID '{screen.ScreenId}'.");
                if (!SupportedFlows.Contains(screen.Flow))
                    throw new InvalidDataException(
                        $"Screen '{screen.ScreenId}' declares unsupported flow '{screen.Flow}'.");
                screen.SourceFile = Path.GetFullPath(fragmentPath);
                screen.SourceDirectory = Path.GetDirectoryName(fragmentPath)!;
                resources.AddScreenSource(screen.ScreenId, fragmentPath);
                foreach (var template in screen.Templates)
                {
                    var path = resources.ResolveFromOrigin(
                        screen.SourceDirectory,
                        template,
                        $"screen '{screen.ScreenId}'");
                    resources.AddSourceAlias(template, path);
                }
                if (!string.IsNullOrWhiteSpace(screen.Recognition.RequiredTemplate))
                {
                    var path = resources.ResolveFromOrigin(
                        screen.SourceDirectory,
                        screen.Recognition.RequiredTemplate,
                        $"screen '{screen.ScreenId}'");
                    resources.AddSourceAlias(screen.Recognition.RequiredTemplate, path);
                }
                merged.Screens.Add(screen);
            }
        }

        if (merged is null)
            throw new InvalidDataException("Career has no screen profile fragments.");
        merged.Screens = merged.Screens
            .OrderBy(screen => screen.Order)
            .ToList();
        return merged;
    }

    private static async Task<HachimiPipelineDefinition> LoadExecutionFragmentsAsync(
        IReadOnlyList<string> fragmentPaths,
        UraScreenProfile profile,
        CareerVisualResourcePackage resources,
        string sharedRoot,
        string scenarioRoot,
        CancellationToken cancellationToken)
    {
        HachimiPipelineDefinition? merged = null;
        var taskSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragmentPath in fragmentPaths)
        {
            var fragment = await HachimiPipelineDefinitionLoader.LoadAsync(fragmentPath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Career execution fragment '{fragmentPath}' is invalid or contains an unsupported property.");
            if (fragment.SchemaVersion != 1)
                throw new InvalidDataException(
                    $"Career execution fragment '{fragmentPath}' must use ordinary pipeline schemaVersion 1.");
            if (fragment.ReferenceWidth != profile.ReferenceWidth
                || fragment.ReferenceHeight != profile.ReferenceHeight)
            {
                throw new InvalidDataException(
                    $"Career execution fragment '{fragmentPath}' uses a different reference resolution.");
            }

            merged ??= new HachimiPipelineDefinition
            {
                Name = fragment.Name,
                SchemaVersion = fragment.SchemaVersion,
                Description = fragment.Description,
                ReferenceWidth = fragment.ReferenceWidth,
                ReferenceHeight = fragment.ReferenceHeight,
                Templates = fragment.Templates,
                Uma = fragment.Uma,
                Timing = fragment.Timing,
                BaseDirectory = sharedRoot,
            };

            var originDirectory = Path.GetDirectoryName(fragmentPath)!;
            foreach (var pair in fragment.Tasks)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || !taskSources.TryAdd(pair.Key, fragmentPath))
                    throw new InvalidDataException(
                        $"Career execution fragments contain a missing or duplicate task ID '{pair.Key}'.");
                var task = pair.Value;
                var originalTemplate = task.Template;
                var originalTransitionTemplates = task.TransitionTemplates.ToArray();
                var resolvedTransitionTemplates = new List<string>(originalTransitionTemplates.Length);
                string? resolvedTemplate = null;
                if (!string.IsNullOrWhiteSpace(task.Template))
                {
                    resolvedTemplate = ResolveFragmentAsset(
                        originDirectory,
                        task.Template,
                        sharedRoot,
                        scenarioRoot,
                        pair.Key);
                    if (!File.Exists(resolvedTemplate))
                        throw new FileNotFoundException("Career execution template was not found.", resolvedTemplate);
                    task.Template = Path.GetRelativePath(sharedRoot, resolvedTemplate)
                        .Replace(Path.DirectorySeparatorChar, '/');
                }
                for (var index = 0; index < task.TransitionTemplates.Count; index++)
                {
                    var transitionTemplate = task.TransitionTemplates[index];
                    var path = ResolveFragmentAsset(
                        originDirectory,
                        transitionTemplate,
                        sharedRoot,
                        scenarioRoot,
                        pair.Key);
                    if (!File.Exists(path))
                        throw new FileNotFoundException("Career transition template was not found.", path);
                    resolvedTransitionTemplates.Add(path);
                    task.TransitionTemplates[index] = Path.GetRelativePath(sharedRoot, path)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    resources.AddSourceAlias(transitionTemplate, path);
                }

                resources.AddTaskSource(
                    pair.Key,
                    fragmentPath,
                    originalTemplate,
                    resolvedTemplate,
                    originalTransitionTemplates,
                    resolvedTransitionTemplates);
                if (originalTemplate is not null && resolvedTemplate is not null)
                {
                    resources.AddSourceAlias(originalTemplate, resolvedTemplate);
                    resources.AddAlias(task.Template!, resolvedTemplate);
                }
                for (var index = 0; index < task.TransitionTemplates.Count; index++)
                    resources.AddAlias(task.TransitionTemplates[index], resolvedTransitionTemplates[index]);
                merged.Tasks.Add(pair.Key, task);
            }
        }

        return merged ?? throw new InvalidDataException("Career has no execution fragments.");
    }

    private static string ResolveFragmentAsset(
        string originDirectory,
        string relativePath,
        string sharedRoot,
        string scenarioRoot,
        string taskId)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException(
                $"Career task '{taskId}' resource '{relativePath}' must be relative to its fragment.");
        var path = Path.GetFullPath(Path.Combine(originDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(path, sharedRoot) && !IsWithin(path, scenarioRoot))
            throw new InvalidDataException(
                $"Career task '{taskId}' resource '{relativePath}' escapes the shared or scenario roots.");
        return path;
    }

    private static async Task LoadCatalogAsync(
        string catalogPath,
        string scenarioRoot,
        string sharedRoot,
        CareerVisualResourcePackage resources,
        CancellationToken cancellationToken)
    {
        await ValidateUniqueCatalogIdsAsync(catalogPath, cancellationToken)
            .ConfigureAwait(false);
        var catalog = await ReadStrictAsync<CareerVisualCatalogDocument>(catalogPath, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Career visual resource catalog is empty.");
        if (catalog.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported Career visual resource catalog schemaVersion {catalog.SchemaVersion}.");
        var baseDirectory = Path.GetDirectoryName(catalogPath)!;
        foreach (var pair in catalog.Assets)
        {
            ValidateRoi(pair.Value.Roi, $"asset '{pair.Key}'");
            ValidateThreshold(pair.Value.Threshold, $"asset '{pair.Key}'");
            var path = ResolveFragmentAsset(baseDirectory, pair.Value.Path, sharedRoot, scenarioRoot, pair.Key);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Career visual asset '{pair.Key}' was not found.", path);
            resources.AddAsset(new CareerVisualResourceDefinition(
                pair.Key,
                pair.Value.Path,
                path,
                pair.Value.Roi,
                pair.Value.Threshold,
                catalogPath));
        }
        foreach (var pair in catalog.Regions)
        {
            ValidateRoi(pair.Value.Roi, $"region '{pair.Key}'");
            ValidateRoi(pair.Value.FocusedRoi, $"region '{pair.Key}' focused ROI");
            ValidateThreshold(pair.Value.Threshold, $"region '{pair.Key}'");
            resources.AddRegion(new CareerVisualRegionDefinition(
                pair.Key,
                pair.Value.Roi,
                pair.Value.FocusedRoi,
                pair.Value.Threshold,
                pair.Value.Metadata,
                catalogPath));
        }
        foreach (var pair in catalog.Collections)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Pattern)
                || !pair.Value.Pattern.Contains('{')
                || !pair.Value.Pattern.Contains('}')
                || Path.IsPathRooted(pair.Value.Pattern))
            {
                throw new InvalidDataException(
                    $"Career visual collection '{pair.Key}' must declare a relative pattern with a variable.");
            }
            var root = ResolveFragmentAsset(baseDirectory, pair.Value.Root, sharedRoot, scenarioRoot, pair.Key);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Career visual collection root was not found: {root}");
            resources.AddCollection(new CareerVisualResourceCollectionDefinition(
                pair.Key,
                root,
                pair.Value.Pattern,
                pair.Value.LegacyPatterns));
        }
        foreach (var pair in catalog.Aliases)
        {
            var path = ResolveFragmentAsset(baseDirectory, pair.Value, sharedRoot, scenarioRoot, pair.Key);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Career resource alias '{pair.Key}' targets a missing file.", path);
            resources.AddAlias(pair.Key, path);
        }
    }

    private static void ValidateComposedPackage(
        UraScreenProfile profile,
        HachimiPipelineDefinition execution,
        CareerVisualResourcePackage resources)
    {
        foreach (var screen in profile.Screens)
        {
            if (screen.Templates.Count == 0)
                throw new InvalidDataException($"Screen '{screen.ScreenId}' has no recognition template.");
            foreach (var template in screen.Templates)
            {
                var path = resources.ResolveScreenTemplate(screen, template);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Screen '{screen.ScreenId}' template was not found.", path);
            }
            if (!string.IsNullOrWhiteSpace(screen.Recognition.RequiredTemplate))
            {
                var path = resources.ResolveScreenTemplate(screen, screen.Recognition.RequiredTemplate);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Screen '{screen.ScreenId}' required template was not found.", path);
            }
            if (!string.IsNullOrWhiteSpace(screen.EntryTask)
                && !execution.Tasks.ContainsKey(screen.EntryTask))
            {
                throw new InvalidDataException(
                    $"Screen '{screen.ScreenId}' entry task '{screen.EntryTask}' is not defined.");
            }

            var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in screen.Actions)
            {
                if (string.IsNullOrWhiteSpace(action.SemanticId)
                    || !actionIds.Add(action.SemanticId)
                    || string.IsNullOrWhiteSpace(action.Task)
                    || !execution.Tasks.ContainsKey(action.Task))
                {
                    throw new InvalidDataException(
                        $"Screen '{screen.ScreenId}' contains an invalid action mapping '{action.SemanticId}'.");
                }
            }
        }

        foreach (var pair in execution.Tasks)
        {
            var task = pair.Value;
            var hasDeclaredTemplateCollection = false;
            if (!string.IsNullOrWhiteSpace(task.TemplateCollection))
            {
                if (!resources.DynamicCollections.ContainsKey(task.TemplateCollection))
                {
                    throw new InvalidDataException(
                        $"Career task '{pair.Key}' references undefined templateCollection "
                        + $"'{task.TemplateCollection}'.");
                }

                hasDeclaredTemplateCollection = true;
            }
            var action = task.Action.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToLowerInvariant();
            if (action is "clickself" or "clickselfuntiltransition" or "wait"
                && string.IsNullOrWhiteSpace(task.Template)
                && !hasDeclaredTemplateCollection)
            {
                throw new InvalidDataException(
                    $"Career task '{pair.Key}' action '{task.Action}' requires a template.");
            }
            if (action == "clickselfuntiltransition" && task.TransitionTemplates.Count == 0)
                throw new InvalidDataException(
                    $"Career task '{pair.Key}' ClickSelfUntilTransition requires transitionTemplates.");
            if (action == "clickrect" && task.SpecificRect is not { Length: 4 })
                throw new InvalidDataException($"Career task '{pair.Key}' ClickRect requires four specificRect values.");
            if (action == "swipe" && task.Swipe is not { Length: 5 })
                throw new InvalidDataException($"Career task '{pair.Key}' Swipe requires five coordinates.");
            if (action == "keyevent" && string.IsNullOrWhiteSpace(task.KeyCode))
                throw new InvalidDataException($"Career task '{pair.Key}' KeyEvent requires keyCode.");
            if (action == "runpipeline" && string.IsNullOrWhiteSpace(task.Pipeline))
                throw new InvalidDataException($"Career task '{pair.Key}' RunPipeline requires pipeline.");
            var algorithm = task.Algorithm.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToLowerInvariant();
            if (algorithm is "parallelmonitor" or "raceresultmonitor"
                && (task.MonitorTasks.Count == 0 || string.IsNullOrWhiteSpace(task.SuccessTask)))
            {
                throw new InvalidDataException(
                    $"Career task '{pair.Key}' monitor requires monitorTasks and successTask.");
            }
            foreach (var reference in task.Next
                         .Concat(task.OnErrorNext)
                         .Concat(task.ExceededNext)
                         .Concat(task.Sub)
                         .Concat(task.MonitorTasks)
                         .Concat(task.AlternativeTemplateTasks)
                         .Concat(task.SuccessTasks)
                         .Append(task.SuccessTask ?? string.Empty)
                         .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!execution.Tasks.ContainsKey(reference))
                    throw new InvalidDataException(
                        $"Task '{pair.Key}' references missing task '{reference}'.");
            }
        }

        foreach (var asset in resources.Assets.Values)
        {
            if (!File.Exists(asset.Path))
                throw new FileNotFoundException($"Career visual asset '{asset.Id}' was not found.", asset.Path);
        }
    }

    private static void ValidateScenarioSelection(
        UraScreenProfile profile,
        CareerVisualResourcePackage resources)
    {
        if (profile.ScenarioSelection is not { } selection)
            return;
        if (selection.Recognition.GetTemplates().Count == 0)
            throw new InvalidDataException("Scenario selection has no recognition template.");
        if (selection.MaxAdvanceAttempts <= 0)
            throw new InvalidDataException("Scenario selection maxAdvanceAttempts must be positive.");
        var originDirectory = profile.ScenarioSelectionSourceDirectory
            ?? throw new InvalidDataException("Scenario selection has no source fragment.");
        foreach (var template in selection.Recognition.GetTemplates())
        {
            var path = resources.ResolveFromOrigin(originDirectory, template, "scenario selection");
            if (!File.Exists(path))
                throw new FileNotFoundException("Scenario selection template was not found.", path);
                    resources.AddSourceAlias(template, path);
        }
    }

    private static void ValidateClawMachine(
        UraScreenProfile profile,
        CareerVisualResourcePackage resources)
    {
        if (string.IsNullOrWhiteSpace(profile.ClawMachine.CreditLabelTemplate))
            return;
        var originDirectory = profile.ClawMachineSourceDirectory
            ?? throw new InvalidDataException("Claw-machine credit label has no source fragment.");
        var path = resources.ResolveFromOrigin(
            originDirectory,
            profile.ClawMachine.CreditLabelTemplate,
            "claw-machine credit label");
        if (!File.Exists(path))
            throw new FileNotFoundException("Claw-machine credit label template was not found.", path);
        resources.AddSourceAlias(profile.ClawMachine.CreditLabelTemplate, path);
    }

    private static string ResolvePackageFile(
        string scenarioRoot,
        string sharedRoot,
        string relativePath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"Career manifest has an empty {description} reference.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Career {description} '{relativePath}' must be relative.");
        var path = Path.GetFullPath(Path.Combine(scenarioRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(path, scenarioRoot) && !IsWithin(path, sharedRoot))
            throw new InvalidDataException(
                $"Career {description} '{relativePath}' escapes the scenario/shared resource roots.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Career {description} was not found.", path);
        return path;
    }

    private static async Task<T?> ReadStrictAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, StrictJsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateUniqueCatalogIdsAsync(
        string catalogPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(catalogPath);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Career visual resource catalog root must be an object.");
        foreach (var sectionName in new[] { "assets", "regions", "collections", "aliases" })
        {
            if (!document.RootElement.TryGetProperty(sectionName, out var section))
                continue;
            if (section.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Career resource catalog '{sectionName}' must be an object.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in section.EnumerateObject())
            {
                if (!ids.Add(item.Name))
                    throw new InvalidDataException(
                        $"Career resource catalog has duplicate {sectionName} ID '{item.Name}'.");
            }
        }
    }

    private static void ValidateRoi(int[]? roi, string description)
    {
        if (roi is not null && (roi.Length != 4 || roi.Any(value => value < 0)))
            throw new InvalidDataException($"Career {description} ROI must contain four non-negative integers.");
    }

    private static void ValidateThreshold(double? threshold, string description)
    {
        if (threshold is double value && (!double.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidDataException($"Career {description} threshold must be between 0 and 1.");
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> SupportedFlows = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "entry", "normal", "independent", "turn", "training", "event", "race", "settlement", "skill",
    };
}
