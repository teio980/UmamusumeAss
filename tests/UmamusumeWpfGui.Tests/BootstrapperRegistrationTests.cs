using StyletIoC;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests;

public sealed class BootstrapperRegistrationTests
{
    [Fact]
    public void MatcherTestBootstrapUsesTheProductionNativeCore()
    {
        Assert.Equal(11u, NativeTemplateMatcherTestBootstrap.CoreApiVersion);
    }

    [Fact]
    public void BootstrapperExtendsTheApplicationRoot()
    {
        var type = typeof(Bootstrapper);
        var baseType = type.BaseType;

        Assert.NotNull(baseType);
        Assert.Contains("Bootstrapper", baseType!.Name);
        Assert.Contains(baseType.GenericTypeArguments, argument => argument == typeof(RootViewModel));
    }

    [Fact]
    public void BootstrapperRegistersTheNativeQueueAndSettingsModules()
    {
        var builder = new StyletIoCBuilder();
        new TestBootstrapper().CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        Assert.NotNull(container.Get<IUmaService>());
        Assert.NotNull(container.Get<IUmaDatabaseService>());
        Assert.IsType<NativeGrassTaskExecutor>(container.Get<IGrassTaskExecutor>());
        Assert.IsType<GrassTaskExecutionService>(container.Get<IGrassTaskExecutionService>());
        Assert.NotNull(container.Get<IGrassTaskCatalog>());
        Assert.NotNull(container.Get<StartGameTaskModule>());
        Assert.NotNull(container.Get<DailyRaceTaskModule>());
        Assert.NotNull(container.Get<CareerTrainingTaskModule>());
    }

    [Fact]
    public void BootstrapperAssemblyDoesNotContainManagedTaskOrCareerRuntimes()
    {
        var typeNames = typeof(Bootstrapper).Assembly.GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("CareerTrainingEngine", typeNames);
        Assert.DoesNotContain("ManagedGrassTaskExecutor", typeNames);
        Assert.DoesNotContain("HachimiJsonPipelineRunner", typeNames);
        Assert.DoesNotContain("AdbIndependentTrainingPipeline", typeNames);
        Assert.DoesNotContain("AdbDailyRacePipeline", typeNames);
    }

    [Fact]
    public void CoreServicesSettingsAndLocalizationAreSingletons()
    {
        var builder = new StyletIoCBuilder();
        new TestBootstrapper().CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        Assert.Same(container.Get<IUmaService>(), container.Get<IUmaService>());
        Assert.Same(container.Get<IConnectionStateService>(), container.Get<IConnectionStateService>());
        Assert.Same(container.Get<ISettingsService>(), container.Get<ISettingsService>());
        Assert.Same(container.Get<ILocalizationService>(), container.Get<ILocalizationService>());
    }
}

internal sealed class TestBootstrapper : Bootstrapper
{
    public void CallConfigureIoC(IStyletIoCBuilder builder) => ConfigureIoC(builder);
}
