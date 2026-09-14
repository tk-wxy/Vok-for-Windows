using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Vok.Infrastructure.Windows.Persistence;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardCompositionRootTests
{
    [Fact]
    public void ProductRegistersOneOwnedImageFeatureAndSharedCaptureLimits()
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
                service.ServiceType == typeof(ClipboardImageFeature));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(ClipboardImageCaptureOptions));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(ClipboardCaptureOptions));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(ClipboardViewModel));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(ClipboardController));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(ClipboardPasteController));
            Assert.Single(services, static service =>
                service.ServiceType == typeof(IClipboardImageDestinationService));

            using var provider = services.BuildServiceProvider();
            var imageOptions = provider.GetRequiredService<ClipboardImageCaptureOptions>();
            var captureOptions = provider.GetRequiredService<ClipboardCaptureOptions>();

            Assert.Same(imageOptions, captureOptions.Image);
            Assert.False(File.Exists(databasePath));
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
