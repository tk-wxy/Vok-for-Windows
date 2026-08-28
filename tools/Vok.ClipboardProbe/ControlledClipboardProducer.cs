using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Vok.ClipboardProbe;

internal sealed partial class ControlledClipboardProducer : IDisposable
{
    private const uint CommandMessage = 0x8000 + 0x52;
    private const uint QuitMessage = 0x0012;
    private const uint RenderFormatMessage = 0x0305;
    private const uint GlobalMoveable = 0x0002;
    private static readonly TimeSpan ShutdownWatchdog = TimeSpan.FromSeconds(5);
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly ConcurrentDictionary<nuint, ProducerCommand> _commands = [];
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProducerWindowProcedure _windowProcedure;
    private readonly Thread _thread;
    private IReadOnlyDictionary<uint, byte[]> _delayedPayloads =
        new Dictionary<uint, byte[]>();
    private nint _window;
    private uint _threadId;
    private long _nextCommandId;
    private int _disposed;

    public ControlledClipboardProducer()
    {
        _windowProcedure = WindowProcedure;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Clipboard Probe Producer",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        _thread.Start();
        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public uint RegisterFormat(string name)
    {
        var format = RegisterClipboardFormat(name);
        return format != 0
            ? format
            : throw LastError($"RegisterClipboardFormat({name})");
    }

    public Task WriteImmediateAsync(
        IReadOnlyList<ProducerFormat> formats,
        CancellationToken cancellationToken) =>
        PostAsync(
            () => WriteImmediate(formats),
            cancellationToken);

    public Task WriteDelayedAsync(
        IReadOnlyList<ProducerFormat> formats,
        CancellationToken cancellationToken) =>
        PostAsync(
            () => WriteDelayed(formats),
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            _ = PostThreadMessage(threadId, QuitMessage, 0, 0);
        }

        if (_thread.IsAlive && !_thread.Join(ShutdownWatchdog))
        {
            throw new TimeoutException("The controlled clipboard producer did not stop.");
        }
    }

    private async Task PostAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var commandId = checked((nuint)Interlocked.Increment(ref _nextCommandId));
        var command = new ProducerCommand(action);
        if (!_commands.TryAdd(commandId, command))
        {
            throw new InvalidOperationException("Unable to queue clipboard producer command.");
        }

        if (!PostMessage(_window, CommandMessage, commandId, 0))
        {
            _commands.TryRemove(commandId, out _);
            throw LastError("PostMessage(producer command)");
        }

        await command.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Run()
    {
        var className = $"Vok.ClipboardProbe.Producer.{Guid.NewGuid():N}";
        nint classNamePointer = 0;
        nint instance = 0;
        var classRegistered = false;
        try
        {
            _threadId = GetCurrentThreadId();
            instance = GetModuleHandle(0);
            classNamePointer = Marshal.StringToHGlobalUni(className);
            var windowClass = new ProducerWindowClass
            {
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = instance,
                ClassName = classNamePointer,
            };
            if (RegisterClass(in windowClass) == 0)
            {
                throw LastError("RegisterClass(producer)");
            }

            classRegistered = true;
            _window = CreateWindowEx(
                0,
                className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                MessageOnlyWindow,
                0,
                instance,
                0);
            if (_window == 0)
            {
                throw LastError("CreateWindowEx(producer)");
            }

            _started.TrySetResult();
            while (true)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == -1)
                {
                    throw LastError("GetMessage(producer)");
                }

                if (result == 0)
                {
                    break;
                }

                _ = DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            foreach (var entry in _commands)
            {
                if (_commands.TryRemove(entry.Key, out var command))
                {
                    command.Fail(exception);
                }
            }
        }
        finally
        {
            if (_window != 0)
            {
                _ = DestroyWindow(_window);
                _window = 0;
            }

            if (classRegistered)
            {
                _ = UnregisterClass(className, instance);
            }

            if (classNamePointer != 0)
            {
                Marshal.FreeHGlobal(classNamePointer);
            }

            _threadId = 0;
        }
    }

    private nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam)
    {
        try
        {
            if (message == CommandMessage)
            {
                if (_commands.TryRemove(wParam, out var command))
                {
                    command.Execute();
                }

                return 0;
            }

            if (message == RenderFormatMessage)
            {
                var format = checked((uint)wParam);
                if (_delayedPayloads.TryGetValue(format, out var payload))
                {
                    SetOwnedClipboardData(format, payload);
                }

                return 0;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void WriteImmediate(IReadOnlyList<ProducerFormat> formats)
    {
        OpenAndEmpty();
        try
        {
            foreach (var format in formats)
            {
                SetOwnedClipboardData(format.Format, format.Payload);
            }
        }
        finally
        {
            CloseOwnedClipboard();
        }
    }

    private void WriteDelayed(IReadOnlyList<ProducerFormat> formats)
    {
        _delayedPayloads = formats.ToDictionary(
            static format => format.Format,
            static format => format.Payload);
        OpenAndEmpty();
        try
        {
            foreach (var format in formats)
            {
                _ = SetClipboardData(format.Format, 0);
            }
        }
        finally
        {
            CloseOwnedClipboard();
        }
    }

    private void OpenAndEmpty()
    {
        if (!OpenClipboard(_window))
        {
            throw LastError("OpenClipboard(producer)");
        }

        if (!EmptyClipboard())
        {
            _ = CloseClipboard();
            throw LastError("EmptyClipboard(producer)");
        }
    }

    private static void CloseOwnedClipboard()
    {
        if (!CloseClipboard())
        {
            throw LastError("CloseClipboard(producer)");
        }
    }

    private static void SetOwnedClipboardData(uint format, byte[] payload)
    {
        var memory = GlobalAlloc(GlobalMoveable, checked((nuint)payload.Length));
        if (memory == 0)
        {
            throw LastError("GlobalAlloc(producer payload)");
        }

        var pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            _ = GlobalFree(memory);
            throw LastError("GlobalLock(producer payload)");
        }

        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
        }
        finally
        {
            _ = GlobalUnlock(memory);
        }

        if (SetClipboardData(format, memory) == 0)
        {
            _ = GlobalFree(memory);
            throw LastError("SetClipboardData(producer payload)");
        }
    }

    private static InvalidOperationException LastError(string operation) =>
        new($"{operation} failed with Win32 error {Marshal.GetLastPInvokeError()}.");

    private sealed class ProducerCommand(Action action)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _completion.Task;

        public void Execute()
        {
            try
            {
                action();
                _completion.TrySetResult();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) =>
            _completion.TrySetException(exception);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint ProducerWindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static partial nint GetModuleHandle(nint moduleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClass(in ProducerWindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(
        out ProducerMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in ProducerMessage message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormat(string formatName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint ownerWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);
}

internal sealed record ProducerFormat(uint Format, byte[] Payload);

[StructLayout(LayoutKind.Sequential)]
internal struct ProducerWindowClass
{
    public uint Style;
    public nint WindowProcedure;
    public int ClassExtraBytes;
    public int WindowExtraBytes;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint BackgroundBrush;
    public nint MenuName;
    public nint ClassName;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ProducerPoint
{
    public readonly int X;
    public readonly int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ProducerMessage
{
    public readonly nint Window;
    public readonly uint Message;
    public readonly nuint WParam;
    public readonly nint LParam;
    public readonly uint Time;
    public readonly ProducerPoint Point;
    public readonly uint Private;
}
