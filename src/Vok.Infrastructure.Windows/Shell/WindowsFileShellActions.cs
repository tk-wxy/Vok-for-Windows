using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Vok.Infrastructure.Windows.Shell;

internal static partial class WindowsFileShellActions
{
    public static void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
        if (started is null)
        {
            throw new InvalidOperationException(
                $"Windows Shell 未能启动目标：{target}");
        }

        started.Dispose();
    }

    public static void RevealInExplorer(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var root = Path.GetPathRoot(target);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(target),
                Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return;
        }

        var pidl = ILCreateFromPath(target);
        if (pidl == 0)
        {
            throw new InvalidOperationException($"无法解析路径：{target}");
        }

        try
        {
            var result = SHOpenFolderAndSelectItems(pidl, 0, 0, 0);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            ILFree(pidl);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ILCreateFromPath(string path);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(nint itemIdList);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(
        nint folderItemIdList,
        uint itemCount,
        nint childItemIdLists,
        uint flags);
}
