using System.IO;
using System.Runtime.InteropServices;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static partial class WindowsClipboardImageDestinationInterop
{
    internal static readonly Guid ShellWindowsClassId =
        new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    internal static readonly Guid FileOperationClassId =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");
    internal static readonly Guid TopLevelBrowserServiceId =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    internal static readonly Guid ShellBrowserInterfaceId =
        new("000214E2-0000-0000-C000-000000000046");
    internal static readonly Guid ShellItemInterfaceId =
        new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid DesktopKnownFolderId =
        new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    internal const uint ShellItemAttributeFolder = 0x20000000;
    internal const uint ShellItemAttributeFileSystem = 0x40000000;
    internal const uint FileSystemPathDisplayName = 0x80058000;

    internal const uint FileOperationSilent = 0x0004;
    internal const uint FileOperationRenameOnCollision = 0x0008;
    internal const uint FileOperationNoConfirmation = 0x0010;
    internal const uint FileOperationNoConfirmMakeDirectory = 0x0200;
    internal const uint FileOperationNoErrorUi = 0x0400;
    internal const uint FileOperationDoNotCopySecurityAttributes = 0x0800;
    internal const uint FileOperationEarlyFailure = 0x00100000;
    internal const uint FileOperationPreserveFileExtensions = 0x00200000;

    internal static object CreateComObject(Guid classId)
    {
        var type = Type.GetTypeFromCLSID(classId, throwOnError: true)
            ?? throw new InvalidOperationException("The requested COM class is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("The requested COM class could not be created.");
    }

    internal static IClipboardShellItem CreateShellItem(string path)
    {
        var interfaceId = ShellItemInterfaceId;
        var result = SHCreateItemFromParsingName(
            path,
            0,
            in interfaceId,
            out var pointer);
        Marshal.ThrowExceptionForHR(result);
        if (pointer == 0)
        {
            throw new InvalidOperationException("Shell item creation returned no interface.");
        }

        return (IClipboardShellItem)GetObjectAndRelease(pointer);
    }

    internal static string? GetFileSystemFolderPath(IClipboardShellItem item) =>
        GetFileSystemPath(item, requireFolder: true);

    internal static string? GetFileSystemItemPath(IClipboardShellItem item) =>
        GetFileSystemPath(item, requireFolder: false);

    private static string? GetFileSystemPath(
        IClipboardShellItem item,
        bool requireFolder)
    {
        var required = ShellItemAttributeFileSystem |
            (requireFolder ? ShellItemAttributeFolder : 0);
        var result = item.GetAttributes(required, out var attributes);
        if (result < 0 || (attributes & required) != required)
        {
            return null;
        }

        result = item.GetDisplayName(FileSystemPathDisplayName, out var pathPointer);
        if (result < 0 || pathPointer == 0)
        {
            return null;
        }

        try
        {
            var path = Marshal.PtrToStringUni(pathPointer);
            return string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    internal static string GetDesktopKnownFolderPath()
    {
        var folderId = DesktopKnownFolderId;
        var result = SHGetKnownFolderPath(in folderId, 0, 0, out var pathPointer);
        Marshal.ThrowExceptionForHR(result);
        if (pathPointer == 0)
        {
            throw new InvalidOperationException("The Desktop known folder returned no path.");
        }

        try
        {
            var path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("The Desktop known folder path is empty.");
            }

            return NormalizePath(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    internal static object GetObjectAndRelease(nint pointer)
    {
        if (pointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointer));
        }

        try
        {
            return Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }
    }

    internal static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    internal static string GetWindowClass(nint window)
    {
        if (window == 0)
        {
            return string.Empty;
        }

        unsafe
        {
            const int capacity = 256;
            var buffer = stackalloc char[capacity];
            var length = GetClassName(window, buffer, capacity);
            return length <= 0 ? string.Empty : new string(buffer, 0, length);
        }
    }

    internal static bool IsSameOrChild(nint parent, nint candidate) =>
        parent != 0 && candidate != 0 &&
        (parent == candidate || IsChild(parent, candidate));

    internal static bool ContainsDescendantClass(nint root, string className)
    {
        if (root == 0 || string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        var pending = new Stack<nint>();
        var visited = new HashSet<nint>();
        pending.Push(root);
        while (pending.TryPop(out var parent) && visited.Count < 4096)
        {
            if (!visited.Add(parent))
            {
                continue;
            }

            var child = nint.Zero;
            while ((child = FindWindowEx(parent, child, 0, 0)) != 0)
            {
                if (string.Equals(
                        GetWindowClass(child),
                        className,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                pending.Push(child);
            }
        }

        return false;
    }

    internal static (uint ThreadId, uint ProcessId) GetWindowIdentity(nint window)
    {
        var threadId = GetWindowThreadProcessId(window, out var processId);
        return (threadId, processId);
    }

    internal static nint ShellWindow => GetShellWindow();

    internal static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid interfaceId,
        out nint shellItem);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        nint token,
        out nint path);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static unsafe partial int GetClassName(
        nint window,
        char* className,
        int maximumCount);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW")]
    private static partial nint FindWindowEx(
        nint parent,
        nint childAfter,
        nint className,
        nint windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint child);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid serviceId, ref Guid interfaceId, out nint result);
}

[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardShellViewWindow
{
    [PreserveSig]
    int GetWindow(out nint window);

    [PreserveSig]
    int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardShellBrowser
{
    [PreserveSig] int GetWindow(out nint window);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    [PreserveSig] int InsertMenusSB(nint menu, nint menuWidths);
    [PreserveSig] int SetMenuSB(nint menu, nint oleMenu, nint activeWindow);
    [PreserveSig] int RemoveMenusSB(nint menu);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? statusText);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
    [PreserveSig] int TranslateAcceleratorSB(nint message, ushort commandId);
    [PreserveSig] int BrowseObject(nint itemIdList, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
    [PreserveSig] int GetControlWindow(uint controlId, out nint window);
    [PreserveSig] int SendControlMsg(uint controlId, uint message, nuint wParam, nint lParam, out nint result);
    [PreserveSig] int QueryActiveShellView(out nint shellView);
}

[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint viewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid interfaceId, out nint folder);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint result);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(uint displayNameType, out nint name);
    [PreserveSig] int GetAttributes(uint requestedAttributes, out uint attributes);
    [PreserveSig] int Compare(nint other, uint hint, out int order);
}

[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardFileOperation
{
    [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] IClipboardFileOperationProgressSink progressSink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint operationFlags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    [PreserveSig] int SetProgressDialog(nint progressDialog);
    [PreserveSig] int SetProperties(nint propertyChangeArray);
    [PreserveSig] int SetOwnerWindow(nint ownerWindow);
    [PreserveSig] int ApplyPropertiesToItem(IClipboardShellItem item);
    [PreserveSig] int ApplyPropertiesToItems(nint items);
    [PreserveSig] int RenameItem(IClipboardShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint progressSink);
    [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    [PreserveSig] int MoveItem(IClipboardShellItem item, IClipboardShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, nint progressSink);
    [PreserveSig] int MoveItems(nint items, IClipboardShellItem destinationFolder);
    [PreserveSig] int CopyItem(IClipboardShellItem item, IClipboardShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, nint progressSink);
    [PreserveSig] int CopyItems(nint items, IClipboardShellItem destinationFolder);
    [PreserveSig] int DeleteItem(IClipboardShellItem item, nint progressSink);
    [PreserveSig] int DeleteItems(nint items);
    [PreserveSig] int NewItem(IClipboardShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, nint progressSink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted(out int anyOperationsAborted);
}

[ComImport]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClipboardFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IClipboardShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostRenameItem(uint flags, IClipboardShellItem? item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int renameResult, IClipboardShellItem? newlyCreated);
    [PreserveSig] int PreMoveItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostMoveItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int moveResult, IClipboardShellItem? newlyCreated);
    [PreserveSig] int PreCopyItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostCopyItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int copyResult, IClipboardShellItem? newlyCreated);
    [PreserveSig] int PreDeleteItem(uint flags, IClipboardShellItem? item);
    [PreserveSig] int PostDeleteItem(uint flags, IClipboardShellItem? item, int deleteResult, IClipboardShellItem? newlyCreated);
    [PreserveSig] int PreNewItem(uint flags, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostNewItem(uint flags, IClipboardShellItem? destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int newResult, IClipboardShellItem? newItem);
    [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}
