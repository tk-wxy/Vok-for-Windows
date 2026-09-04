using System.Globalization;
using System.IO;

namespace Vok.Infrastructure.Windows.Shell;

internal enum WindowsFileReferenceKind
{
    File,
    Directory,
    ShellLink,
}

internal sealed record WindowsFileReference(
    WindowsFileReferenceKind Kind,
    string NormalizedPath,
    string DisplayName,
    string Fingerprint);

internal static class WindowsFileReferenceResolver
{
    public static WindowsFileReference ResolveExisting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var kind = isDirectory
            ? WindowsFileReferenceKind.Directory
            : string.Equals(
                Path.GetExtension(fullPath),
                ".lnk",
                StringComparison.OrdinalIgnoreCase)
                ? WindowsFileReferenceKind.ShellLink
                : WindowsFileReferenceKind.File;
        var normalizedPath = isDirectory
            ? Path.TrimEndingDirectorySeparator(fullPath)
            : fullPath;
        return new WindowsFileReference(
            kind,
            normalizedPath,
            GetDisplayName(normalizedPath, kind),
            GetFingerprint(normalizedPath, attributes));
    }

    public static string GetFingerprint(string path)
    {
        var attributes = File.GetAttributes(path);
        return GetFingerprint(path, attributes);
    }

    private static string GetDisplayName(
        string path,
        WindowsFileReferenceKind kind)
    {
        var name = Path.GetFileName(path);
        if (kind is WindowsFileReferenceKind.ShellLink &&
            name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string GetFingerprint(
        string path,
        FileAttributes attributes)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        var length = (attributes & FileAttributes.Directory) != 0
            ? 0
            : new FileInfo(path).Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{lastWrite}:{length}:{(int)attributes}");
    }
}
