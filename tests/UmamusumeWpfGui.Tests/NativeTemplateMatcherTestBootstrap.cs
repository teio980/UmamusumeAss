using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests;

internal static class NativeTemplateMatcherTestBootstrap
{
    private static readonly object Gate = new();
    private static UmaService? _service;
    private static string? _appDataDirectory;

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void InitializeForTestAssembly() => Initialize();
#pragma warning restore CA2255

    internal static uint CoreApiVersion
    {
        get
        {
            Initialize();
            return _service?.CoreApiVersion
                ?? throw new InvalidOperationException("The native Core API version is unavailable.");
        }
    }

    internal static void Initialize()
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
            string nativeDll = Path.Combine(AppContext.BaseDirectory, "UmamusumeCore.dll");
            if (!File.Exists(nativeDll))
            {
                throw new FileNotFoundException(
                    "The WPF test assembly requires the staged native recognition runtime.",
                    nativeDll);
            }

            _appDataDirectory = Path.Combine(repositoryRoot, "build", "native-template-matcher-test",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_appDataDirectory);
            var service = new UmaService(new InlineEventDispatcher());
            try
            {
                service.InitializeAsync(repositoryRoot, _appDataDirectory).GetAwaiter().GetResult();
                if (service.CoreApiVersion != 11)
                {
                    throw new InvalidOperationException(
                        $"The WPF matcher tests require native Core API 11; received {service.CoreApiVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
                }

                Type matcher = typeof(GrayImage).Assembly.GetType(
                    "UmamusumeWpfGui.Services.Tasks.TemplateMatcher", throwOnError: true)!;
                MethodInfo configure = matcher.GetMethod(
                    "ConfigureNativeRecognition", BindingFlags.Static | BindingFlags.NonPublic)!;
                configure.Invoke(null, [service]);
                _service = service;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            catch
            {
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
                DeleteAppDataDirectory();
                throw;
            }
        }
    }

    internal static UmaDatabaseService CreateDatabaseFromCoreCatalog()
    {
        Initialize();
        var service = _service
            ?? throw new InvalidOperationException("The native Core test service is unavailable.");
        var catalog = service.GetCatalogAsync("all").GetAwaiter().GetResult();
        var database = new UmaDatabaseService();
        database.LoadFromCoreCatalog(catalog, Path.Combine(FindRepositoryRoot(AppContext.BaseDirectory), "resource"));
        return database;
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            if (_service is not { } service)
            {
                return;
            }

            _service = null;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            try
            {
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                DeleteAppDataDirectory();
            }
        }
    }

    private static void OnProcessExit(object? sender, EventArgs eventArgs) => Shutdown();

    private static void DeleteAppDataDirectory()
    {
        if (_appDataDirectory is { } directory && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
        _appDataDirectory = null;
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "UmamusumeWpfGui")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the UmamusumeAss repository root.");
    }

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
