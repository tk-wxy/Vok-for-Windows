using System.Runtime.InteropServices;

namespace Vok.ClipboardProbe;

internal sealed partial class IsolatedWindowStation : IDisposable
{
    private const uint WindowStationAllAccess = 0x0000037F;
    private const uint DesktopAllAccess = 0x000001FF;

    private nint _windowStation;
    private nint _desktop;

    private IsolatedWindowStation(nint windowStation, nint desktop)
    {
        _windowStation = windowStation;
        _desktop = desktop;
    }

    public static IsolatedWindowStation Create()
    {
        // This probe is a dedicated child process. Window-station APIs must be
        // the first USER32 calls so it never connects to the interactive station.
        var windowStation = CreateWindowStation(
            null,
            0,
            WindowStationAllAccess,
            0);
        if (windowStation == 0)
        {
            throw LastError("CreateWindowStation");
        }

        if (!SetProcessWindowStation(windowStation))
        {
            _ = CloseWindowStation(windowStation);
            throw LastError("SetProcessWindowStation");
        }

        var desktop = CreateDesktop(
            "default",
            null,
            0,
            0,
            DesktopAllAccess,
            0);
        if (desktop == 0)
        {
            _ = CloseWindowStation(windowStation);
            throw LastError("CreateDesktop");
        }

        return new IsolatedWindowStation(windowStation, desktop);
    }

    public void Dispose()
    {
        if (_desktop != 0)
        {
            _ = CloseDesktop(_desktop);
            _desktop = 0;
        }

        if (_windowStation != 0)
        {
            _ = CloseWindowStation(_windowStation);
            _windowStation = 0;
        }
    }

    private static InvalidOperationException LastError(string operation) =>
        new($"{operation} failed with Win32 error {Marshal.GetLastPInvokeError()}.");

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowStationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowStation(
        string? name,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWindowStation(nint windowStation);

    [LibraryImport("user32.dll", EntryPoint = "CreateDesktopW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateDesktop(
        string desktop,
        string? device,
        nint deviceMode,
        uint flags,
        uint desiredAccess,
        nint securityAttributes);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseWindowStation(nint windowStation);
}
