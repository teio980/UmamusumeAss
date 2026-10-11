using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Linq.Expressions;
using System.Text.Json;
using Xunit.Abstractions;
using Xunit.Sdk;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: TemplateMatcherGoldenRunner <test-assembly> [--native] [--offline] [class-name-filter ...]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var testDirectory = Path.GetDirectoryName(assemblyPath)!;
var offline = args.Contains("--offline", StringComparer.OrdinalIgnoreCase);
var filters = args.Skip(1)
    .Where(argument => !argument.Equals("--native", StringComparison.OrdinalIgnoreCase)
        && !argument.Equals("--offline", StringComparison.OrdinalIgnoreCase))
    .ToArray();
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(testDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
string? testBootstrapType = null;
MethodInfo? shutdownBootstrap = null;
try
{
    var stagedNativeDll = Path.Combine(testDirectory, "UmamusumeCore.dll");
    if (!File.Exists(stagedNativeDll))
        throw new FileNotFoundException("The matcher test assembly requires the staged UmamusumeCore.dll.", stagedNativeDll);
    var runnerNativeDll = Path.Combine(AppContext.BaseDirectory, "UmamusumeCore.dll");
    if (!string.Equals(Path.GetFullPath(stagedNativeDll), Path.GetFullPath(runnerNativeDll), StringComparison.OrdinalIgnoreCase))
        File.Copy(stagedNativeDll, runnerNativeDll, overwrite: true);

    // Loading the test assembly invokes its module initializer, which sets up the same
    // idempotent native test service used by ordinary WPF test runs.
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    testBootstrapType = "UmamusumeWpfGui.Tests.NativeTemplateMatcherTestBootstrap";
    var bootstrap = assembly.GetType(testBootstrapType, throwOnError: true)!;
    const BindingFlags staticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
    var initializeBootstrap = bootstrap.GetMethod("Initialize", staticNonPublic)!;
    shutdownBootstrap = bootstrap.GetMethod("Shutdown", staticNonPublic)!;
    initializeBootstrap.Invoke(null, null);
    var coreApiVersion = (uint)bootstrap.GetProperty("CoreApiVersion", staticNonPublic)!.GetValue(null)!;
    if (coreApiVersion != 11)
        throw new InvalidOperationException($"The Phase 5 matcher runner requires native Core API 11; received {coreApiVersion}.");

    var appAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(testDirectory, "UmamusumeAss.dll"));
    appAssembly.GetType("UmamusumeWpfGui.Services.ResourcePathRuntime")!.GetMethod("SetBaseDirectory")!
        .Invoke(null, new object[] { testDirectory });

    var tracePath = Environment.GetEnvironmentVariable("TM_GOLDEN_TRACE")
        ?? Path.Combine(testDirectory, "template-matcher-trace.jsonl");
    TraceCapture.Configure(appAssembly, tracePath, assemblyPath, filters, coreApiVersion);
    Console.WriteLine($"Native matcher enabled with UmaAssistantCore API {coreApiVersion}.");

    using var framework = new XunitTestFramework(new NullMessageSink());
    using var discoverer = framework.GetDiscoverer(new ReflectionAssemblyInfo(assembly));
    var options = new Options();
    options.SetValue("xunit.execution.DisableParallelization", true);
    var discovery = new DiscoverySink(filters, offline);
    discoverer.Find(false, discovery, options);
    await discovery.Done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    TraceCapture.WriteDiscovery(discovery.Cases);
    Console.WriteLine($"Discovered {discovery.Cases.Count} matching test cases; excluded {discovery.Excluded} Live/Packaging cases.");
    if (discovery.Cases.Count == 0)
        return 2;

    using var executor = framework.GetExecutor(assembly.GetName());
    var execution = new ExecutionSink();
    executor.RunTests(discovery.Cases, execution, options);
    ITestAssemblyFinished summary;
    try
    {
        summary = await execution.Done.Task.WaitAsync(TimeSpan.FromMinutes(45));
    }
    catch (TimeoutException)
    {
        TraceCapture.WriteRunSummary("timed_out", discovery.Cases.Count, execution.Passed, execution.Failed, execution.Skipped);
        Console.Error.WriteLine("Test runner timed out after 45 minutes; completed outcomes and matcher inputs were flushed.");
        return 124;
    }

    TraceCapture.WriteRunSummary("completed", summary.TestsRun, execution.Passed, execution.Failed, execution.Skipped);
    Console.WriteLine($"Tests: {summary.TestsRun}, passed: {summary.TestsRun-summary.TestsFailed-summary.TestsSkipped}, failed: {summary.TestsFailed}, skipped: {summary.TestsSkipped}");
    return summary.TestsFailed == 0 && execution.Errors == 0 ? 0 : 1;
}
catch (Exception exception)
{
    TraceCapture.WriteRunnerError(exception);
    Console.Error.WriteLine(exception);
    return 2;
}
finally
{
    shutdownBootstrap?.Invoke(null, null);
    TraceCapture.Close();
}

sealed class Options : ITestFrameworkDiscoveryOptions, ITestFrameworkExecutionOptions
{
    readonly Dictionary<string, object?> values = new();
    public T GetValue<T>(string name) => values.TryGetValue(name, out var value) && value is T typed ? typed : default!;
    public void SetValue<T>(string name, T value) => values[name] = value;
}

sealed class DiscoverySink(string[] filters, bool offline) : IMessageSink
{
    public List<ITestCase> Cases { get; } = new();
    public int Excluded { get; private set; }
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is ITestCaseDiscoveryMessage item
            && (filters.Length == 0 || filters.Any(filter =>
                item.TestCase.TestMethod.TestClass.Class.Name.Contains(filter, StringComparison.Ordinal)
                || item.TestCase.DisplayName.Contains(filter, StringComparison.Ordinal))))
        {
            if (offline && item.TestCase.Traits.TryGetValue("Category", out var categories)
                && categories.Any(category => category.Equals("Live", StringComparison.OrdinalIgnoreCase)
                    || category.Equals("Packaging", StringComparison.OrdinalIgnoreCase)))
                Excluded++;
            else
                Cases.Add(item.TestCase);
        }
        if (message is IDiscoveryCompleteMessage)
            Done.TrySetResult(true);
        if (message is IErrorMessage error)
            Console.WriteLine(string.Join("\n", error.Messages));
        return true;
    }
}

sealed class ExecutionSink : IMessageSink
{
    public TaskCompletionSource<ITestAssemblyFinished> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Errors;
    public int Passed;
    public int Failed;
    public int Skipped;

    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is ITestStarting started)
            TraceCapture.CurrentTest = started.Test.DisplayName;
        if (message is ITestPassed passed)
        {
            Passed++;
            TraceCapture.WriteOutcome(passed.Test.DisplayName, "passed", null);
        }
        if (message is ITestFailed failed)
        {
            Failed++;
            TraceCapture.WriteOutcome(failed.Test.DisplayName, "failed", string.Join("\n", failed.Messages));
            Console.WriteLine($"FAIL {failed.Test.DisplayName}\n{string.Join("\n", failed.Messages)}\n{string.Join("\n", failed.StackTraces)}");
        }
        if (message is ITestSkipped skipped)
        {
            Skipped++;
            TraceCapture.WriteOutcome(skipped.Test.DisplayName, "skipped", skipped.Reason);
        }
        if (message is ITestFinished)
            TraceCapture.CurrentTest = "";
        if (message is IErrorMessage error)
        {
            Errors++;
            Console.WriteLine(string.Join("\n", error.Messages));
        }
        if (message is ITestAssemblyFinished finished)
            Done.TrySetResult(finished);
        return true;
    }
}

static class TraceCapture
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> WrittenInputs = new(StringComparer.OrdinalIgnoreCase);
    private static StreamWriter? _writer;
    private static string _inputDirectory = "";
    private static string _repoRoot = "";
    private static string _currentTest = "";

    public static string CurrentTest
    {
        get => Volatile.Read(ref _currentTest);
        set => Volatile.Write(ref _currentTest, value);
    }

    public static void Configure(
        Assembly appAssembly,
        string outputPath,
        string testAssemblyPath,
        string[] filters,
        uint coreApiVersion)
    {
        _repoRoot = FindRepositoryRoot(Path.GetDirectoryName(testAssemblyPath)!);
        var fullTracePath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTracePath)!);
        _inputDirectory = Path.Combine(Path.GetDirectoryName(fullTracePath)!, Path.GetFileNameWithoutExtension(fullTracePath) + "-inputs");
        Directory.CreateDirectory(_inputDirectory);
        _writer = new StreamWriter(new FileStream(fullTracePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        Write(new
        {
            recordType = "run",
            status = "started",
            native = true,
            backend = "UmaAssistantCore",
            coreApiVersion,
            testAssembly = Path.GetRelativePath(_repoRoot, testAssemblyPath),
            sourceRoot = _repoRoot,
            filters,
            startedUtc = DateTimeOffset.UtcNow,
        });

        var matcher = appAssembly.GetType("UmamusumeWpfGui.Services.Tasks.TemplateMatcher", throwOnError: true)!;
        var property = matcher.GetProperty("TraceObserver", BindingFlags.Static | BindingFlags.NonPublic)!;
        var delegateType = property.PropertyType;
        var traceType = delegateType.GetGenericArguments()[0];
        var parameter = Expression.Parameter(traceType, "trace");
        var writeMethod = typeof(TraceCapture).GetMethod(nameof(WriteMatch), BindingFlags.Static | BindingFlags.Public)!;
        property.SetValue(null, Expression.Lambda(delegateType,
            Expression.Call(writeMethod, Expression.Convert(parameter, typeof(object))), parameter).Compile());
    }

    public static void WriteDiscovery(IReadOnlyCollection<ITestCase> cases)
    {
        foreach (var testCase in cases)
            Write(new { recordType = "discovered", test = testCase.DisplayName, className = testCase.TestMethod.TestClass.Class.Name });
        Write(new { recordType = "discovery_summary", count = cases.Count });
    }

    public static void WriteOutcome(string test, string status, string? message) =>
        Write(new { recordType = "outcome", test, status, message });

    public static void WriteRunSummary(string status, int total, int passed, int failed, int skipped) =>
        Write(new { recordType = "run_summary", status, total, passed, failed, skipped, completedUtc = DateTimeOffset.UtcNow });

    public static void WriteRunnerError(Exception exception) =>
        Write(new { recordType = "runner_error", exception = exception.ToString(), completedUtc = DateTimeOffset.UtcNow });

    public static void WriteMatch(object trace)
    {
        lock (Gate)
        {
            var type = trace.GetType();
            var screen = GetProperty<object>(type, trace, "ScreenInput");
            var template = GetProperty<object?>(type, trace, "TemplateInput");
            var screenPath = SaveInput(screen, GetProperty<string?>(type, trace, "ScreenSha256")
                ?? throw new InvalidDataException("Matcher trace has no screen hash."));
            var templatePath = template is null
                ? NormalizeSourcePath(GetProperty<string?>(type, trace, "TemplatePath"))
                : SaveInput(template, GetProperty<string?>(type, trace, "TemplateSha256")
                    ?? throw new InvalidDataException("Matcher trace has no template hash."));
            Write(new
            {
                recordType = "match",
                test = CurrentTest,
                method = GetProperty<string>(type, trace, "Method"),
                screenSha256 = GetProperty<string>(type, trace, "ScreenSha256"),
                templateSha256 = GetProperty<string?>(type, trace, "TemplateSha256"),
                screenWidth = GetProperty<int>(type, trace, "ScreenWidth"),
                screenHeight = GetProperty<int>(type, trace, "ScreenHeight"),
                templateWidth = GetProperty<int>(type, trace, "TemplateWidth"),
                templateHeight = GetProperty<int>(type, trace, "TemplateHeight"),
                roi = GetProperty<int[]?>(type, trace, "Roi"),
                threshold = GetProperty<double>(type, trace, "Threshold"),
                referenceWidth = GetProperty<int>(type, trace, "ReferenceWidth"),
                referenceHeight = GetProperty<int>(type, trace, "ReferenceHeight"),
                parameters = GetProperty<string>(type, trace, "Parameters"),
                found = GetProperty<bool>(type, trace, "Found"),
                score = GetProperty<double>(type, trace, "Score"),
                x = GetProperty<int>(type, trace, "X"),
                y = GetProperty<int>(type, trace, "Y"),
                width = GetProperty<int>(type, trace, "Width"),
                height = GetProperty<int>(type, trace, "Height"),
                screenInput = screenPath,
                templateInput = templatePath,
            });
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string SaveInput(object image, string hash)
    {
        var fullPath = Path.Combine(_inputDirectory, hash + ".uimg.gz");
        if (WrittenInputs.Add(hash) && !File.Exists(fullPath))
        {
            var type = image.GetType();
            var width = GetProperty<int>(type, image, "Width");
            var height = GetProperty<int>(type, image, "Height");
            var pixels = GetProperty<byte[]>(type, image, "Pixels");
            var rgba = GetProperty<byte[]?>(type, image, "RgbaPixels");
            using var file = File.Create(fullPath);
            using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
            using var writer = new BinaryWriter(gzip);
            writer.Write(new byte[] { (byte)'U', (byte)'I', (byte)'M', (byte)'G', 1, 0, 0, 0 });
            writer.Write(width);
            writer.Write(height);
            writer.Write(pixels.Length);
            writer.Write(rgba?.Length ?? -1);
            writer.Write(pixels);
            if (rgba is not null)
                writer.Write(rgba);
        }
        return Path.GetRelativePath(_repoRoot, fullPath).Replace('\\', '/');
    }

    private static string? NormalizeSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) && fullPath.StartsWith(_repoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(_repoRoot, fullPath).Replace('\\', '/')
            : path;
    }

    private static T GetProperty<T>(Type type, object instance, string name)
    {
        var value = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance);
        return value is null ? default! : (T)value;
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "UmamusumeWpfGui")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root from the test assembly directory.");
    }

    private static void Write<T>(T value)
    {
        lock (Gate)
        {
            if (_writer is null)
                return;
            _writer.WriteLine(JsonSerializer.Serialize(value));
        }
    }
}
