using System.Runtime.Loader;
using Xunit.Abstractions;
using Xunit.Sdk;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: BridgeTestRunner <test-assembly.dll> [class-name-filter ...]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"Test assembly does not exist: {assemblyPath}");
    return 2;
}

var testDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(testDirectory, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
using var framework = new XunitTestFramework(new NullMessageSink());
using var discoverer = framework.GetDiscoverer(new ReflectionAssemblyInfo(assembly));
var options = new RunnerOptions();
options.SetValue("xunit.execution.DisableParallelization", true);
var discovery = new DiscoverySink(args.Skip(1).ToArray());
discoverer.Find(false, discovery, options);
await discovery.Done.Task.WaitAsync(TimeSpan.FromSeconds(60));
Console.WriteLine($"Discovered {discovery.Cases.Count} matching xUnit test cases.");
if (discovery.Errors > 0)
{
    return 1;
}

if (discovery.Cases.Count == 0)
{
    return 2;
}

using var executor = framework.GetExecutor(assembly.GetName());
var execution = new ExecutionSink();
executor.RunTests(discovery.Cases, execution, options);
var summary = await execution.Done.Task.WaitAsync(TimeSpan.FromMinutes(5));
Console.WriteLine($"xUnit: total={summary.TestsRun}, passed={summary.TestsRun - summary.TestsFailed - summary.TestsSkipped}, failed={summary.TestsFailed}, skipped={summary.TestsSkipped}");
return summary.TestsFailed == 0 && execution.Errors == 0 ? 0 : 1;

sealed class RunnerOptions : ITestFrameworkDiscoveryOptions, ITestFrameworkExecutionOptions
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T GetValue<T>(string name) => _values.TryGetValue(name, out object? value) ? (T)value! : default!;

    public void SetValue<T>(string name, T value) => _values[name] = value;
}

sealed class DiscoverySink(string[] filters) : IMessageSink
{
    public List<ITestCase> Cases { get; } = [];
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Errors { get; private set; }

    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is ITestCaseDiscoveryMessage item
            && (filters.Length == 0 || filters.Any(filter =>
                item.TestCase.TestMethod.TestClass.Class.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
        {
            Cases.Add(item.TestCase);
        }

        if (message is IDiscoveryCompleteMessage)
        {
            Done.TrySetResult(true);
        }

        if (message is IErrorMessage error)
        {
            Errors++;
            Console.Error.WriteLine(string.Join(Environment.NewLine, error.Messages));
        }

        return true;
    }
}

sealed class ExecutionSink : IMessageSink
{
    public TaskCompletionSource<ITestAssemblyFinished> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Errors { get; private set; }

    public bool OnMessage(IMessageSinkMessage message)
    {
        if (message is ITestStarting starting)
        {
            Console.WriteLine($"RUN {starting.Test.DisplayName}");
        }

        if (message is ITestFailed failed)
        {
            Console.WriteLine($"FAIL {failed.Test.DisplayName}\n{string.Join(Environment.NewLine, failed.Messages)}\n{string.Join(Environment.NewLine, failed.StackTraces)}");
        }

        if (message is ITestSkipped skipped)
        {
            Console.WriteLine($"SKIP {skipped.Test.DisplayName}: {skipped.Reason}");
        }

        if (message is IErrorMessage error)
        {
            Errors++;
            Console.Error.WriteLine($"ERROR {string.Join(Environment.NewLine, error.Messages)}");
        }

        if (message is ITestAssemblyFinished finished)
        {
            Done.TrySetResult(finished);
        }

        return true;
    }
}
