using System.Runtime.InteropServices;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed partial class Win32ClipboardNativeApi : IWindowsClipboardNativeApi
{
    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public uint RegisterFormat(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var format = RegisterClipboardFormat(name);
        if (format == 0)
        {
            throw new InvalidOperationException(
                $"Unable to register clipboard format '{name}' (Win32 error {GetLastError()}).");
        }

        return format;
    }

    public bool Open(nint ownerWindow) => OpenClipboard(ownerWindow);

    public bool Close() => CloseClipboard();

    public bool Empty() => EmptyClipboard();

    public uint EnumerateFormats(uint previousFormat) =>
        EnumClipboardFormats(previousFormat);

    public unsafe string? GetFormatName(uint format)
    {
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        var length = GetClipboardFormatName(format, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    public nint GetData(uint format) => GetClipboardData(format);

    public nint SetData(uint format, nint handle) => SetClipboardData(format, handle);

    public nint AllocateGlobal(nuint bytes) => GlobalAlloc(0x0042, bytes);

    public nint FreeGlobal(nint handle) => GlobalFree(handle);

    public nuint GetGlobalSize(nint handle) => GlobalSize(handle);

    public nint LockGlobal(nint handle) => GlobalLock(handle);

    public void UnlockGlobal(nint handle)
    {
        _ = GlobalUnlock(handle);
    }

    public void ResetLastError() => SetLastError(0);

    public int GetLastError() => Marshal.GetLastPInvokeError();

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormat(string formatName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint ownerWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", SetLastError = true)]
    private static unsafe partial int GetClipboardFormatName(
        uint format,
        char* formatName,
        int maximumCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial void SetLastError(uint errorCode);
}
