using System.Text.Json;
using System.Diagnostics;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;

namespace Umamusume.Avalonia.Services;

public sealed record DeveloperResourceFile(string Path, string RelativePath)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public override string ToString() => RelativePath;
}

public sealed record DeveloperNativeToolResult(
    bool Available,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? ToolPath = null);

/// <summary>
/// File and native Core operations used by the Avalonia developer tools.
/// This service deliberately uses the application's existing IUmaService.
/// </summary>
public sealed class DeveloperToolsService
{
    private readonly IUmaService _coreService;
    private readonly IUmaAutomationService _automationService;

    public DeveloperToolsService(IUmaAutomationService automationService, IUmaService coreService)
    {
        ArgumentNullException.ThrowIfNull(automationService);
        ArgumentNullException.ThrowIfNull(coreService);
        _automationService = automationService;
        _coreService = coreService;
    }

    public string ResourceRoot => Path.GetFullPath(
        _automationService.ResourcePath
        ?? _coreService.ResourcePath
        ?? PlatformPaths.GetActiveResourceDirectory());

    /// <summary>Maps a bundled resource path into the writable active resource view.</summary>
    public string ResolveResourcePath(string path) => _automationService.ResolveActiveResourcePath(path);

    public string HachimiRoot => Path.Combine(ResourceRoot, "hachimi");

    public bool IsCoreReady => !string.IsNullOrWhiteSpace(_coreService.CoreVersion);

    public string? CoreVersion => _coreService.CoreVersion;

    public IReadOnlyList<DeveloperResourceFile> GetPipelineFiles()
    {
        var directory = Path.Combine(HachimiRoot, "pipelines");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(CreateResourceFile)
            .ToArray();
    }

    public IReadOnlyList<DeveloperResourceFile> GetTemplateImages()
    {
        if (!Directory.Exists(HachimiRoot)) return [];
        return Directory.EnumerateFiles(HachimiRoot, "*", SearchOption.AllDirectories)
            .Where(IsImageFile)
            .OrderBy(path => Path.GetRelativePath(HachimiRoot, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new DeveloperResourceFile(path, Path.GetRelativePath(ResourceRoot, path)))
            .ToArray();
    }

    public IReadOnlyList<DeveloperResourceFile> GetTemplateDirectories()
    {
        if (!Directory.Exists(HachimiRoot)) return [];
        return Directory.EnumerateFiles(HachimiRoot, "*", SearchOption.AllDirectories)
            .Where(IsImageFile)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DeveloperResourceFile(path, Path.GetRelativePath(ResourceRoot, path)))
            .ToArray();
    }

    public IReadOnlyList<DeveloperResourceFile> GetReplayFixtures()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Directory.GetParent(ResourceRoot)?.FullName ?? ResourceRoot, "tools", "replay-fixtures"),
            Path.Combine(AppContext.BaseDirectory, "tools", "replay-fixtures"),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
            {
                candidates.Add(Path.Combine(directory.FullName, "tools", "replay-fixtures"));
                break;
            }
        }

        string? fixtureDirectory = candidates
            .Where(Directory.Exists)
            .FirstOrDefault(path => Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly).Any());
        if (fixtureDirectory is null) return [];
        return Directory.EnumerateFiles(fixtureDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path)))
            .ToArray();
    }

    public IReadOnlyList<DeveloperResourceFile> GetScenarioManifests()
    {
        if (!Directory.Exists(HachimiRoot)) return [];
        return Directory.EnumerateFiles(HachimiRoot, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(ResourceRoot, path), StringComparer.OrdinalIgnoreCase)
            .Select(CreateResourceFile)
            .ToArray();
    }

    public IReadOnlyList<DeveloperResourceFile> GetScenarioPackageFiles(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string canonicalManifest = ResolveResourcePath(manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(canonicalManifest));
        string packageRoot = Path.GetDirectoryName(canonicalManifest)!;
        string resourceBoundary = Path.GetFullPath(ResourceRoot) + Path.DirectorySeparatorChar;
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonicalManifest };
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectManifestPaths(document.RootElement, packageRoot, resourceBoundary, filePaths, directoryPaths);
        foreach (string directory in directoryPaths.ToArray())
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                filePaths.Add(Path.GetFullPath(path));
        }

        return filePaths
            .Where(File.Exists)
            .OrderBy(path => Path.GetRelativePath(ResourceRoot, path), StringComparer.OrdinalIgnoreCase)
            .Select(CreateResourceFile)
            .ToArray();
    }

    public static string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadAllText(path);
    }

    public static async Task SaveTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, new System.Text.UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task<bool> SaveResourceTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default) =>
        _automationService.SaveResourceTextAsync(ResolveResourcePath(path), text, cancellationToken);

    public string? ValidatePipelineJson(string json)
    {
        EnsureCoreReady();
        return _coreService.ValidatePipelineJson(json);
    }

    public Task<PipelineRunResult> RunPipelineAsync(
        string pipelinePath,
        string? entryTask,
        CancellationToken cancellationToken = default)
    {
        EnsureCoreReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelinePath);
        string activePath = ResolveResourcePath(pipelinePath);
        if (!File.Exists(activePath))
            throw new FileNotFoundException("Save the pipeline before running it.", activePath);
        // CoreBridge accepts an existing path here and uses its directory as the pipeline base.
        // Passing parsed JSON would change relative template resolution to user_directory.
        return _coreService.RunPipelineAsync(Path.GetFullPath(activePath), entryTask, cancellationToken);
    }

    public RecognitionImage? LoadTemplateImage(string path)
    {
        EnsureCoreReady();
        return _coreService.LoadTemplateImage(ResolveResourcePath(path));
    }

    public TemplateMatchResult MatchTemplate(
        RecognitionImage screen,
        RecognitionImage template,
        TemplateMatchOptions options)
    {
        EnsureCoreReady();
        return _coreService.MatchTemplate(screen, template, options);
    }

    public Task ReloadResourcesAsync(CancellationToken cancellationToken = default)
    {
        EnsureCoreReady();
        string basePath = Directory.GetParent(ResourceRoot)?.FullName
            ?? throw new InvalidOperationException("The native resource directory has no parent directory.");
        return _coreService.ReloadResourceAsync(basePath, cancellationToken);
    }

    public static async Task<DeveloperNativeToolResult> RunOfflineReplayAsync(
        string pipelinePath,
        string fixturePath,
        string? entryTask,
        CancellationToken cancellationToken = default)
    {
        string? toolPath = FindPipelineReplayTool();
        if (toolPath is null)
            return new DeveloperNativeToolResult(false, -1, string.Empty,
                "The native offline replay tool is unavailable; uma_pipeline_replay was not found.");
        if (!File.Exists(pipelinePath)) throw new FileNotFoundException("Pipeline JSON file not found.", pipelinePath);
        if (!File.Exists(fixturePath)) throw new FileNotFoundException("Replay fixture not found.", fixturePath);
        var arguments = new List<string> { Path.GetFullPath(pipelinePath), Path.GetFullPath(fixturePath) };
        if (!string.IsNullOrWhiteSpace(entryTask)) arguments.Add(entryTask);
        return await RunNativeToolAsync(toolPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeveloperNativeToolResult> InspectScenarioPackageAsync(
        string manifestPath,
        IReadOnlyDictionary<string, string> unsavedFiles,
        CancellationToken cancellationToken = default)
    {
        string? toolPath = FindPipelineReplayTool();
        if (toolPath is null)
            return new DeveloperNativeToolResult(false, -1, string.Empty,
                "The native career package inspector is unavailable; uma_pipeline_replay was not found.");

        string requestPath = Path.Combine(Path.GetTempPath(), $"career-package-inspect-{Guid.NewGuid():N}.json");
        try
        {
            var activeManifestPath = ResolveResourcePath(manifestPath);
            var request = new
            {
                resource_root = ResourceRoot,
                files = unsavedFiles.Select(pair => new
                {
                    path = Path.GetFullPath(ResolveResourcePath(pair.Key)),
                    raw_json = pair.Value
                }).ToArray()
            };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request),
                new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return await RunNativeToolAsync(toolPath,
                ["--inspect-career-package", activeManifestPath, requestPath],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(requestPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public string? ValidateScenarioPackage(string manifestPath, IEnumerable<DeveloperResourceFile> packageFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(packageFiles);

        manifestPath = ResolveResourcePath(manifestPath);
        var knownPaths = packageFiles.Select(file => Path.GetFullPath(ResolveResourcePath(file.Path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in knownPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return $"{Path.GetRelativePath(ResourceRoot, path)}: {ex.Message}";
            }
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object)
                return "Scenario manifest must be a JSON object.";
            if (!manifest.RootElement.TryGetProperty("scenarioId", out var scenarioId)
                || scenarioId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(scenarioId.GetString()))
                return "Scenario manifest is missing a valid scenarioId.";

            var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectJsonFileReferences(manifest.RootElement, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, referencedFiles);
            foreach (string path in referencedFiles)
            {
                if (!File.Exists(path))
                    return $"Manifest references a missing file: {Path.GetRelativePath(ResourceRoot, path)}";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return null;
    }

    private void EnsureCoreReady()
    {
        if (!IsCoreReady)
            throw new InvalidOperationException("Initialize the native Core service before running this diagnostic. No device connection is required.");
    }

    private static string? FindPipelineReplayTool()
    {
        string executableName = OperatingSystem.IsWindows() ? "uma_pipeline_replay.exe" : "uma_pipeline_replay";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "tools", executableName),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "CMakePresets.json"))) continue;
            candidates.Add(Path.Combine(directory.FullName, "build", "msvc-debug", "tools", "Debug", executableName));
            candidates.Add(Path.Combine(directory.FullName, "build", "msvc-release", "tools", "Release", executableName));
            candidates.Add(Path.Combine(directory.FullName, "build", "test-cmake", "tools", executableName));
            candidates.Add(Path.Combine(directory.FullName, "build", "osx-arm64", "tools", executableName));
            candidates.Add(Path.Combine(directory.FullName, "build", "osx-arm64-debug", "tools", executableName));
            break;
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<DeveloperNativeToolResult> RunNativeToolAsync(
        string toolPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Could not start native tool {toolPath}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The native replay or inspection tool exceeded 30 seconds.");
        }
        return new DeveloperNativeToolResult(true, process.ExitCode,
            await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false), toolPath);
    }

    private DeveloperResourceFile CreateResourceFile(string path) =>
        new(Path.GetFullPath(path), Path.GetRelativePath(ResourceRoot, path));

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tif" or ".tiff";

    private static void CollectManifestPaths(
        JsonElement element,
        string packageRoot,
        string resourceBoundary,
        ISet<string> files,
        ISet<string> directories)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                CollectManifestPaths(property.Value, packageRoot, resourceBoundary, files, directories);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectManifestPaths(item, packageRoot, resourceBoundary, files, directories);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Contains(':')) return;
            string normalized = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !normalized.EndsWith(Path.DirectorySeparatorChar)) return;

            string resolved = Path.GetFullPath(Path.Combine(packageRoot, normalized));
            if (!IsWithin(resolved, packageRoot) && !resolved.StartsWith(resourceBoundary, StringComparison.OrdinalIgnoreCase))
                return;
            if (resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                files.Add(resolved);
            else
                directories.Add(resolved);
        }
    }

    private static void CollectJsonFileReferences(JsonElement element, string packageRoot, ISet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                CollectJsonFileReferences(property.Value, packageRoot, paths);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectJsonFileReferences(item, packageRoot, paths);
        }
        else if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { } value
            && value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !value.Contains(':'))
        {
            paths.Add(Path.GetFullPath(Path.Combine(packageRoot, value.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
    }
}
