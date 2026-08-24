using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vok.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Vok.Infrastructure.Windows.Input;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x564F;
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);

    private readonly IForegroundWindowService _foregroundWindowService;
    private readonly GlobalHotkeyGesture _gesture;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _messageThread;
    private int _lifecycle;
    private uint _messageThreadId;

    public GlobalHotkeyService(
        IForegroundWindowService foregroundWindowService,
        GlobalHotkeyGesture gesture)
    {
        _foregroundWindowService = foregroundWindowService;
        _gesture = gesture;
        _messageThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "Vok.GlobalHotkey",
        };
    }

    public event EventHandler<HotkeyInvocation>? Pressed;

    public event EventHandler<Exception>? Faulted;

    public string DisplayGesture => _gesture.DisplayName;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }

        if (previous == 0)
        {
            _messageThread.Start();
        }

        try
        {
            await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _lifecycle, 2) == 2)
        {
            return;
        }

        var threadId = Volatile.Read(ref _messageThreadId);
        if (threadId != 0)
        {
            _ = PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);
        }

        if (_messageThread.IsAlive && !_messageThread.Join(ShutdownWatchdog))
        {
            PublishFault(new TimeoutException(
                $"Global hotkey thread did not stop within the {ShutdownWatchdog.TotalSeconds:0}-second shutdown watchdog."));
        }
    }

    private void RunMessageLoop()
    {
        var registered = false;
        try
        {
            Volatile.Write(ref _messageThreadId, PInvoke.GetCurrentThreadId());
            _ = PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            registered = PInvoke.RegisterHotKey(
                HWND.Null,
                HotkeyId,
                ToNativeModifiers(_gesture.Modifiers) | HOT_KEY_MODIFIERS.MOD_NOREPEAT,
                _gesture.VirtualKey);

            if (!registered)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Unable to register {DisplayGesture}. The gesture may already be in use.");
            }

            _started.TrySetResult();

            while (true)
            {
                var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);
                if (result.Value == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed.");
                }

                if (result.Value == 0)
                {
                    break;
                }

                if (message.message == PInvoke.WM_HOTKEY && message.wParam.Value == HotkeyId)
                {
                    var receivedTimestamp = Stopwatch.GetTimestamp();
                    PublishPressed(new HotkeyInvocation(
                        _foregroundWindowService.Capture(),
                        receivedTimestamp));
                }
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            PublishFault(exception);
        }
        finally
        {
            if (registered)
            {
                _ = PInvoke.UnregisterHotKey(HWND.Null, HotkeyId);
            }

            Volatile.Write(ref _messageThreadId, 0);
        }
    }

    private void PublishPressed(HotkeyInvocation invocation)
    {
        var handlers = Pressed?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<HotkeyInvocation>>())
        {
            try
            {
                handler(this, invocation);
            }
            catch (Exception exception)
            {
                PublishFault(exception);
            }
        }
    }

    private static HOT_KEY_MODIFIERS ToNativeModifiers(GlobalHotkeyModifiers modifiers)
    {
        var result = (HOT_KEY_MODIFIERS)0;
        if ((modifiers & GlobalHotkeyModifiers.Alt) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_ALT;
        }
        if ((modifiers & GlobalHotkeyModifiers.Control) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        }
        if ((modifiers & GlobalHotkeyModifiers.Shift) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        }
        if ((modifiers & GlobalHotkeyModifiers.Windows) != 0)
        {
            result |= HOT_KEY_MODIFIERS.MOD_WIN;
        }

        return result;
    }

    private void PublishFault(Exception exception)
    {
        Trace.TraceError(exception.ToString());
        var handlers = Faulted?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<Exception>>())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception subscriberException)
            {
                Trace.TraceError(subscriberException.ToString());
            }
        }
    }
}
