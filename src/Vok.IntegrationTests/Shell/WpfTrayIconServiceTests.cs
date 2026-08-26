using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Features.Tray;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Shell;

[Collection(WpfApplicationCollection.Name)]
public sealed class WpfTrayIconServiceTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void StartsAndDisposesOnOwningStaThread()
    {
        wpf.Invoke(() =>
        {
            var service = new WpfTrayIconService(
                new FakeForegroundWindowService(),
                new FakeTrayMenuService());

            service.Start();
            service.Start();
            service.Dispose();
            service.Dispose();

            Assert.Throws<ObjectDisposedException>(service.Start);
        });
    }

    [Fact]
    public void ContextCommandUsesForegroundCapturedBeforeMenuOpened()
    {
        wpf.Invoke(() =>
        {
            var first = new ForegroundTarget((nint)41, 7, 11);
            var foreground = new FakeForegroundWindowService(first);
            using var service = new WpfTrayIconService(
                foreground,
                new FakeTrayMenuService());
            TrayIconCommandEventArgs? invocation = null;
            service.CommandInvoked += (_, args) => invocation = args;

            service.CaptureContextInvocation();
            foreground.Target = new ForegroundTarget((nint)43, 13, 17);
            service.InvokeContextCommand(TrayIconCommand.OpenSettings);

            Assert.NotNull(invocation);
            Assert.Equal(TrayIconCommand.OpenSettings, invocation.Command);
            Assert.Equal(first, invocation.ForegroundTarget);
            Assert.True(invocation.NativeTimestamp > 0);
            Assert.Equal(1, foreground.CaptureCount);
        });
    }

    [Fact]
    public void ClosingMenuPreventsStaleForegroundFromCrossingInvocations()
    {
        wpf.Invoke(() =>
        {
            var foreground = new FakeForegroundWindowService(
                new ForegroundTarget((nint)41, 7, 11));
            using var service = new WpfTrayIconService(
                foreground,
                new FakeTrayMenuService());
            TrayIconCommandEventArgs? invocation = null;
            service.CommandInvoked += (_, args) => invocation = args;

            service.CaptureContextInvocation();
            service.ResetContextInvocation();
            var second = new ForegroundTarget((nint)43, 13, 17);
            foreground.Target = second;
            service.InvokeContextCommand(TrayIconCommand.ShowWorkspace);

            Assert.NotNull(invocation);
            Assert.Equal(second, invocation.ForegroundTarget);
            Assert.Equal(2, foreground.CaptureCount);
        });
    }

    [Fact]
    public void SubscriberFailureDoesNotSuppressRemainingSubscribers()
    {
        wpf.Invoke(() =>
        {
            using var service = new WpfTrayIconService(
                new FakeForegroundWindowService(),
                new FakeTrayMenuService());
            Exception? reported = null;
            var completed = false;
            service.CommandInvoked += (_, _) => throw new InvalidOperationException("subscriber");
            service.CommandInvoked += (_, _) => completed = true;
            service.Faulted += (_, exception) => reported = exception;

            service.InvokeShowWorkspace();

            Assert.True(completed);
            Assert.IsType<InvalidOperationException>(reported);
        });
    }

    [Fact]
    public void NativeMenuSelectionUsesPreMenuForegroundAndClearsItAfterward()
    {
        wpf.Invoke(() =>
        {
            var first = new ForegroundTarget((nint)41, 7, 11);
            var second = new ForegroundTarget((nint)43, 13, 17);
            var foreground = new FakeForegroundWindowService(first);
            var menu = new FakeTrayMenuService
            {
                NextCommand = TrayIconCommand.OpenSettings,
            };
            using var service = new WpfTrayIconService(foreground, menu);
            var invocations = new List<TrayIconCommandEventArgs>();
            service.CommandInvoked += (_, args) => invocations.Add(args);
            service.Start();

            service.CaptureContextInvocation();
            foreground.Target = second;
            service.InvokeContextMenu(keyboardInvocation: false);
            menu.NextCommand = TrayIconCommand.ShowWorkspace;
            service.InvokeContextMenu(keyboardInvocation: false);

            Assert.Equal(2, invocations.Count);
            Assert.Equal(first, invocations[0].ForegroundTarget);
            Assert.Equal(second, invocations[1].ForegroundTarget);
            Assert.Equal(2, foreground.CaptureCount);
            Assert.Equal(2, menu.ShowCount);
            Assert.NotEqual(0, menu.LastOwnerWindow);
            Assert.NotEqual(Guid.Empty, menu.LastIconId);
        });
    }

    private sealed class FakeForegroundWindowService(ForegroundTarget? target = null)
        : IForegroundWindowService
    {
        public ForegroundTarget Target { get; set; } = target ?? ForegroundTarget.None;

        public int CaptureCount { get; private set; }

        public ForegroundTarget Capture()
        {
            CaptureCount++;
            return Target;
        }

        public ForegroundRestoreResult Restore(ForegroundTarget target) =>
            new(ForegroundRestoreStatus.NotRequested, 0);
    }

    private sealed class FakeTrayMenuService : ITrayMenuService
    {
        public TrayIconCommand? NextCommand { get; set; }

        public int ShowCount { get; private set; }

        public nint LastOwnerWindow { get; private set; }

        public Guid LastIconId { get; private set; }

        public TrayIconCommand? Show(
            nint ownerWindow,
            Guid iconId,
            bool keyboardInvocation)
        {
            ShowCount++;
            LastOwnerWindow = ownerWindow;
            LastIconId = iconId;
            return NextCommand;
        }
    }
}
