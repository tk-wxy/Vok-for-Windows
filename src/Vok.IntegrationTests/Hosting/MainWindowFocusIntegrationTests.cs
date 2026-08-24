using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Vok.Presentation;
using Xunit;

namespace Vok.IntegrationTests.Hosting;

public sealed class MainWindowFocusIntegrationTests
{
    [Fact]
    public void InputAnchorFocusClearsAcrossHideAndCanBeReacquiredOnNextShow()
    {
        RunOnSta(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x21));
            application.Resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(0xA9, 0xB0, 0xC2));
            application.Resources["AccentBrush"] = new SolidColorBrush(Color.FromRgb(0x86, 0xA7, 0xFF));
            var window = new MainWindow(new MainWindowViewModel());
            try
            {
                window.Show();
                _ = window.Activate();
                Assert.True(window.FocusInputAnchor());
                Assert.True(window.HasInputAnchorFocus);

                window.Hide();
                Assert.False(window.HasInputAnchorFocus);

                window.Show();
                _ = window.Activate();
                Assert.True(window.FocusInputAnchor());
                Assert.True(window.HasInputAnchorFocus);
            }
            finally
            {
                window.Close();
                application.Shutdown();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
