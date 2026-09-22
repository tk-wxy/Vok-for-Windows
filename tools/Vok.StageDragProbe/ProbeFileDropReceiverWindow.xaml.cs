using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Vok.StageDragProbe;

internal sealed partial class ProbeFileDropReceiverWindow : Window
{
    private readonly ProbeFileDropReceiver _receiver;
    private readonly bool _closeOnTerminal;
    private DispatcherOperation? _pendingClose;
    private bool _closed;

    internal ProbeFileDropReceiverWindow(ProbeFileDropReceiver receiver, bool closeOnTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        _receiver = receiver;
        _closeOnTerminal = closeOnTerminal;
        InitializeComponent();
        PolicyText.Text = $"接收策略：{receiver.Policy}";
        RefreshStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _receiver.MarkReady(new WindowInteropHelper(this).Handle.ToInt64());
        RefreshStatus();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _receiver.MarkShown();
        RefreshStatus();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        e.Effects = _receiver.Enter(e.Data, e.AllowedEffects);
        RefreshStatus();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = _receiver.Over(e.AllowedEffects);
        RefreshStatus();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        _receiver.Leave();
        RefreshStatus();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        e.Effects = _receiver.Drop(e.Data, e.AllowedEffects);
        RefreshStatus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _receiver.Cancel();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) _receiver.CloseWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _pendingClose?.Abort();
        _pendingClose = null;
        _receiver.Dispose();
        base.OnClosed(e);
    }

    private void RefreshStatus()
    {
        if (_receiver.Report is { } report)
        {
            StatusText.Text = $"{report.Status} · {report.Reason} · {report.Paths.Count} 项";
            if (_closeOnTerminal && !_closed && _pendingClose is null)
            {
                // Leave the routed event with its final effect before closing its HWND.
                // This is event-driven dispatch, not a timed guess at OLE completion.
                _pendingClose = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!_closed) Close();
                }));
            }
        }
    }
}
