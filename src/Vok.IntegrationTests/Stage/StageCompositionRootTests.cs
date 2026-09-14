using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.Infrastructure.Windows.Launcher;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageCompositionRootTests
{
    [Fact]
    public async Task ProductRegistersStageServicesWithSharedInfrastructure()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        var databasePath = Path.Combine(directory, "composition.db");
        try
        {
            App.RegisterServices(
                services,
                () => new SqliteStorageExecutor(databasePath));

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
            Assert.Single(services, static service =>
                service.ServiceType == typeof(IStageRelinkTargetPicker));

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            var shellExecutor = provider.GetRequiredService<WindowsShellStaExecutor>();

            var repository = provider.GetRequiredService<IStageRepository>();
            _ = await repository.LoadPageAsync(
                after: null,
                pageSize: 1,
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(databasePath));
            Assert.NotNull(provider.GetRequiredService<IStageFileService>());
            Assert.Same(
                provider.GetRequiredService<StageViewModel>(),
                provider.GetRequiredService<StageViewModel>());
            Assert.Same(
                shellExecutor,
                provider.GetRequiredService<WindowsShellStaExecutor>());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
