using Microsoft.Extensions.DependencyInjection;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardCompositionRootTests
{
    [Fact]
    public void ProductRegistersOneOwnedImageFeatureAndSharedCaptureLimits()
    {
        var services = new ServiceCollection();

        App.RegisterServices(services);

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

        using var provider = services.BuildServiceProvider();
        var imageOptions = provider.GetRequiredService<ClipboardImageCaptureOptions>();
        var captureOptions = provider.GetRequiredService<ClipboardCaptureOptions>();

        Assert.Same(imageOptions, captureOptions.Image);
    }
}
