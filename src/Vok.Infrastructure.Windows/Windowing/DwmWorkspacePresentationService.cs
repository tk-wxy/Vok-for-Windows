using System.Collections.Concurrent;
using System.Diagnostics;
using Vok.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Vok.Infrastructure.Windows.Windowing;

public sealed class DwmWorkspacePresentationService : IWorkspacePresentationService
{
    private const int FenceQueueCapacity = 1;
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<long> _fenceRequests = new(FenceQueueCapacity);
    private readonly Thread _fenceThread;
    private int _disposed;

    public DwmWorkspacePresentationService()
    {
        _fenceThread = new Thread(RunFenceLoop)
        {
            IsBackground = true,
            Name = "Vok.DwmPresentationFence",
        };
        _fenceThread.Start();
    }

    public event EventHandler<WorkspacePresentationFenceCompletedEventArgs>? FenceCompleted;

    public unsafe void SetCloaked(nint workspaceWindow, bool cloaked)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(workspaceWindow);

        var value = cloaked ? 1u : 0u;
        PInvoke.DwmSetWindowAttribute(
            new HWND((void*)workspaceWindow),
            DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
            &value,
            sizeof(uint)).ThrowOnFailure();
    }

    public unsafe bool IsCloaked(nint workspaceWindow)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(workspaceWindow);

        uint value = 0;
        PInvoke.DwmGetWindowAttribute(
            new HWND((void*)workspaceWindow),
            DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &value,
            sizeof(uint)).ThrowOnFailure();
        return value != 0;
    }

    public bool TryRequestFence(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        try
        {
            return _fenceRequests.TryAdd(generation);
        }
        catch (InvalidOperationException) when (_fenceRequests.IsAddingCompleted)
        {
            throw new ObjectDisposedException(nameof(DwmWorkspacePresentationService));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _fenceRequests.CompleteAdding();
        if (!_fenceThread.Join(ShutdownWatchdog))
        {
            Trace.TraceError(
                $"DWM presentation fence thread did not stop within the " +
                $"{ShutdownWatchdog.TotalSeconds:0}-second shutdown watchdog.");
            return;
        }

        _fenceRequests.Dispose();
    }

    private void RunFenceLoop()
    {
        foreach (var generation in _fenceRequests.GetConsumingEnumerable())
        {
            Exception? failure = null;
            try
            {
                PInvoke.DwmFlush().ThrowOnFailure();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            PublishFenceCompleted(generation, failure);
        }
    }

    private void PublishFenceCompleted(long generation, Exception? failure)
    {
        var handlers = FenceCompleted?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new WorkspacePresentationFenceCompletedEventArgs(generation, failure);
        foreach (var handler in handlers.Cast<EventHandler<WorkspacePresentationFenceCompletedEventArgs>>())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
            }
        }
    }
}
