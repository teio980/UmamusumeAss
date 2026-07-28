using StyletIoC;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests;

public sealed class BootstrapperRegistrationTests
{
    [Fact]
    public void Bootstrapper_ClassExists()
    {
        var type = typeof(Bootstrapper);
        Assert.NotNull(type);
    }

    [Fact]
    public void Bootstrapper_ExtendsBootstrapperOfRootViewModel()
    {
        var type = typeof(Bootstrapper);
        var baseType = type.BaseType;
        Assert.NotNull(baseType);
        Assert.Contains("Bootstrapper", baseType!.Name);
        Assert.Contains(baseType.GenericTypeArguments, t => t == typeof(RootViewModel));
    }

    [Fact]
    public void Bootstrapper_ConfigureIoC_RegistersAllServices()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        Assert.NotNull(container.Get<IConnectionStateService>());
        Assert.NotNull(container.Get<IUmaService>());
        Assert.NotNull(container.Get<IEventDispatcher>());
        Assert.NotNull(container.Get<ISettingsService>());
        Assert.NotNull(container.Get<ILocalizationService>());
        Assert.NotNull(container.Get<IWinAdapter>());

        Assert.NotNull(container.Get<LogViewModel>());
        Assert.NotNull(container.Get<SettingsViewModel>());
        Assert.NotNull(container.Get<RootViewModel>());
    }

    [Fact]
    public void Bootstrapper_UmaService_IsSingleton()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<IUmaService>();
        var second = container.Get<IUmaService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Bootstrapper_ConnectionStateService_IsSingleton()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<IConnectionStateService>();
        var second = container.Get<IConnectionStateService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Bootstrapper_SettingsService_IsSingleton()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<ISettingsService>();
        var second = container.Get<ISettingsService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Bootstrapper_LocalizationService_IsSingleton()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<ILocalizationService>();
        var second = container.Get<ILocalizationService>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Bootstrapper_LogViewModel_IsSingleton()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<LogViewModel>();
        var second = container.Get<LogViewModel>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Bootstrapper_SettingsViewModel_IsTransient()
    {
        var builder = new StyletIoCBuilder();
        var bootstrapper = new TestBootstrapper();
        bootstrapper.CallConfigureIoC(builder);
        var container = builder.BuildContainer();

        var first = container.Get<SettingsViewModel>();
        var second = container.Get<SettingsViewModel>();

        Assert.NotSame(first, second);
    }
}

internal sealed class TestBootstrapper : Bootstrapper
{
    public void CallConfigureIoC(IStyletIoCBuilder builder)
    {
        ConfigureIoC(builder);
    }
}
