using System.Runtime.InteropServices;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static partial class ClipboardListenerNativeMethods
{
    internal const uint ClipboardUpdateMessage = 0x031D;
    internal const uint QuitMessage = 0x0012;
    internal const uint CaptureMessage = 0x8000 + 0x43;
    internal const uint CommandMessage = 0x8000 + 0x44;
    private static readonly nint MessageOnlyWindow = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    internal static WindowClassRegistration RegisterWindowClass(
        string className,
        WindowProcedure procedure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(procedure);

        var instance = GetModuleHandle(0);
        var classNamePointer = Marshal.StringToHGlobalUni(className);
        try
        {
            var windowClass = new NativeWindowClass
            {
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance,
                ClassName = classNamePointer,
            };
            var atom = RegisterClass(in windowClass);
            if (atom == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClass failed with Win32 error {GetLastError()}.");
            }

            return new WindowClassRegistration(className, instance);
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    internal static nint CreateMessageOnlyWindow(
        WindowClassRegistration registration)
    {
        var window = CreateWindowEx(
            0,
            registration.ClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            MessageOnlyWindow,
            0,
            registration.Instance,
            0);
        if (window == 0)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed with Win32 error {GetLastError()}.");
        }

        return window;
    }

    internal static bool AddListener(nint window) =>
        AddClipboardFormatListener(window);

    internal static bool RemoveListener(nint window) =>
        RemoveClipboardFormatListener(window);

    internal static bool Destroy(nint window) => DestroyWindow(window);

    internal static bool Unregister(WindowClassRegistration registration) =>
        UnregisterClass(registration.ClassName, registration.Instance);

    internal static uint GetCurrentNativeThreadId() => GetCurrentThreadId();

    internal static NativeMessageResult GetNextMessage()
    {
        var result = GetMessage(out var message, 0, 0, 0);
        return new NativeMessageResult(result, message);
    }

    internal static void Dispatch(in NativeMessage message) =>
        _ = DispatchMessage(in message);

    internal static bool PostWindowMessage(nint window, uint message) =>
        PostMessage(window, message, 0, 0);

    internal static bool PostQuit(uint threadId) =>
        PostThreadMessage(threadId, QuitMessage, 0, 0);

    internal static nint DefaultWindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam) =>
        DefWindowProc(window, message, wParam, lParam);

    internal static int GetLastError() => Marshal.GetLastPInvokeError();

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static partial nint GetModuleHandle(nint moduleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static partial ushort RegisterClass(in NativeWindowClass windowClass);

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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(
        out NativeMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in NativeMessage message);

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
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWindowClass
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
internal readonly struct NativePoint
{
    public readonly int X;
    public readonly int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeMessage
{
    public readonly nint Window;
    public readonly uint Message;
    public readonly nuint WParam;
    public readonly nint LParam;
    public readonly uint Time;
    public readonly NativePoint Point;
    public readonly uint Private;
}

internal readonly record struct NativeMessageResult(
    int Result,
    NativeMessage Message);

internal readonly record struct WindowClassRegistration(
    string ClassName,
    nint Instance);
