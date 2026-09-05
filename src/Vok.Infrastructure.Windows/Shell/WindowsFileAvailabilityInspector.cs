using System.IO;

namespace Vok.Infrastructure.Windows.Shell;

internal enum WindowsFileAvailability
{
    Unknown,
    Available,
    Missing,
    Unavailable,
}

internal static class WindowsFileAvailabilityInspector
{
    public static WindowsFileAvailability Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return WindowsFileAvailability.Unavailable;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return WindowsFileAvailability.Unknown;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) &&
                new DriveInfo(root).DriveType is DriveType.Network)
            {
                return WindowsFileAvailability.Unknown;
            }

            _ = File.GetAttributes(path);
            return WindowsFileAvailability.Available;
        }
        catch (FileNotFoundException)
        {
            return WindowsFileAvailability.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return WindowsFileAvailability.Missing;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                UnauthorizedAccessException or NotSupportedException)
        {
            return WindowsFileAvailability.Unavailable;
        }
    }
}
