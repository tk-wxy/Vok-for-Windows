using Microsoft.Extensions.DependencyInjection;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Launcher;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageCompositionRootTests
{
    [Fact]
    public void ProductRegistersStageServicesWithSharedInfrastructure()
    {
        var services = new ServiceCollection();

        App.RegisterServices(services);

        Assert.Single(services, static service =>
            service.ServiceType == typeof(StageOptions));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(IStageRepository));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(IStageFileService));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(IStageClipboardService));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(StageViewModel));
        Assert.Single(services, static service =>
            service.ServiceType == typeof(StageActionController));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var shellExecutor = provider.GetRequiredService<WindowsShellStaExecutor>();

        Assert.NotNull(provider.GetRequiredService<IStageRepository>());
        Assert.NotNull(provider.GetRequiredService<IStageFileService>());
        Assert.Same(
            provider.GetRequiredService<StageViewModel>(),
            provider.GetRequiredService<StageViewModel>());
        Assert.Same(
            shellExecutor,
            provider.GetRequiredService<WindowsShellStaExecutor>());
    }
}
