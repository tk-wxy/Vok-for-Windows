using System.Runtime.InteropServices;

namespace Vok.Infrastructure.Windows.Shell;

internal static partial class WindowsTrayRegistrationNativeMethods
{
    public static int GetIconRectangle(
        nint ownerWindow,
        Guid iconId,
        out TrayIconRectangle rectangle)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = ownerWindow,
            Id = 0,
            IconId = iconId,
        };
        return ShellNotifyIconGetRect(in identifier, out rectangle);
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static partial int ShellNotifyIconGetRect(
        in NotifyIconIdentifier identifier,
        out TrayIconRectangle iconLocation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public Guid IconId;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct TrayIconRectangle
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
