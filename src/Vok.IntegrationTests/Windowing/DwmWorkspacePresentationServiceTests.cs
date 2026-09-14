using System.Windows;
using System.Windows.Interop;
using Vok.Core.Platform;
using Vok.Infrastructure.Windows.Windowing;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Windowing;

[Collection(WpfApplicationCollection.Name)]
public sealed class DwmWorkspacePresentationServiceTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void CloakStateRoundTripsOnAnOwnedWpfHwnd()
    {
        wpf.Invoke(() =>
        {
            var window = new Window { ShowActivated = false };
            using var service = new DwmWorkspacePresentationService();
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();

                service.SetCloaked(handle, cloaked: true);
                Assert.True(service.IsCloaked(handle));

                service.SetCloaked(handle, cloaked: false);
                Assert.False(service.IsCloaked(handle));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DedicatedWorkerPublishesGenerationScopedFenceCompletion()
    {
        using var completed = new ManualResetEventSlim();
        using var service = new DwmWorkspacePresentationService();
        WorkspacePresentationFenceCompletedEventArgs? observed = null;
        service.FenceCompleted += (_, eventArgs) =>
        {
            observed = eventArgs;
            completed.Set();
        };

        Assert.True(service.TryRequestFence(42, 7));
        Assert.True(completed.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.NotNull(observed);
        Assert.Equal(42, observed.Generation);
        Assert.Equal(7, observed.Attempt);
        Assert.Null(observed.Exception);
    }
}
