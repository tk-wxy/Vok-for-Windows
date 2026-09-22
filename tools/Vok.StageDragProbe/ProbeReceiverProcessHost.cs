using System.Windows;
using System.Windows.Threading;

namespace Vok.StageDragProbe;

internal static class ProbeReceiverProcessHost
{
    internal static void Run(ProbeFileDropReceiver receiver)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Exception? failure = null;
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            failure ??= args.Exception;
            args.Handled = true;
            receiver.FailHost(args.Exception);
            // A failed UI loop is not resumed after reporting. Shutdown closes even
            // cancelled Closing events, on the owner STA, without a timed fallback.
            application.Shutdown(1);
        }

        application.DispatcherUnhandledException += Unhandled;
        try
        {
            application.Run(new ProbeFileDropReceiverWindow(receiver, closeOnTerminal: true));
        }
        finally
        {
            application.DispatcherUnhandledException -= Unhandled;
            if (!application.Dispatcher.HasShutdownStarted) application.Shutdown(1);
        }

        if (failure is not null) throw new InvalidOperationException("The receiver Dispatcher failed.", failure);
    }
}
