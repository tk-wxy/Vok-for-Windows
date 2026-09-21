using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vok.StageDragProbe;

internal sealed record ProbePathEnvironment(
    string ProductRoot,
    string UserProfile)
{
    internal static ProbePathEnvironment Capture()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "The local application data or user profile path is unavailable.");
        }

        return new ProbePathEnvironment(
            StageDragProbeOptions.NormalizePath(
                Path.Combine(localApplicationData, "Vok")),
            StageDragProbeOptions.NormalizePath(userProfile));
    }
}

internal static class ProbePathPolicy
{
    internal static void Validate(
        StageDragProbeOptions options,
        ProbePathEnvironment environment,
        Func<string, FileAttributes>? readAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        readAttributes ??= File.GetAttributes;

        if (!Directory.Exists(options.RepositoryRoot) ||
            !File.Exists(Path.Combine(options.RepositoryRoot, "src", "Vok.sln")))
        {
            throw new DirectoryNotFoundException(
                $"The repository root is invalid: {options.RepositoryRoot}");
        }

        if (!Directory.Exists(options.AllowedRoot))
        {
            throw new DirectoryNotFoundException(
                $"The allowed output root does not exist: {options.AllowedRoot}");
        }

        var outputParent = Path.GetDirectoryName(options.OutputRoot);
        if (string.IsNullOrWhiteSpace(outputParent) ||
            !PathEquals(
                StageDragProbeOptions.NormalizePath(outputParent),
                options.AllowedRoot))
        {
            throw new InvalidOperationException(
                "The output root must be a direct child of the allowed output root.");
        }

        if (Directory.Exists(options.OutputRoot) || File.Exists(options.OutputRoot))
        {
            throw new IOException($"The output root already exists: {options.OutputRoot}");
        }

        RejectProtectedPath(options.AllowedRoot, options.RepositoryRoot, "repository");
        RejectProtectedPath(options.OutputRoot, options.RepositoryRoot, "repository");
        RejectProtectedPath(options.AllowedRoot, environment.ProductRoot, "product");
        RejectProtectedPath(options.OutputRoot, environment.ProductRoot, "product");
        if (PathEquals(options.AllowedRoot, environment.UserProfile) ||
            PathEquals(options.OutputRoot, environment.UserProfile))
        {
            throw new InvalidOperationException(
                "The user profile itself cannot be used as a probe output root.");
        }

        EnsureNoReparsePoint(options.RepositoryRoot, readAttributes);
        EnsureNoReparsePoint(options.AllowedRoot, readAttributes);
    }

    internal static bool IsSameOrDescendant(string candidate, string root)
    {
        var normalizedCandidate = StageDragProbeOptions.NormalizePath(candidate);
        var normalizedRoot = StageDragProbeOptions.NormalizePath(root);
        if (PathEquals(normalizedCandidate, normalizedRoot))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectProtectedPath(
        string candidate,
        string protectedRoot,
        string description)
    {
        if (IsSameOrDescendant(candidate, protectedRoot))
        {
            throw new InvalidOperationException(
                $"The probe output cannot be placed in the {description} root: {candidate}");
        }
    }

    private static void EnsureNoReparsePoint(
        string path,
        Func<string, FileAttributes> readAttributes)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"The path has no volume root: {path}");
        }

        var relative = Path.GetRelativePath(root, path);
        var current = root;
        if ((readAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Probe paths cannot traverse a reparse point: {current}");
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((readAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Probe paths cannot traverse a reparse point: {current}");
            }
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            StageDragProbeOptions.NormalizePath(left),
            StageDragProbeOptions.NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record ProbeOutputDirectory(string Path)
{
    internal static ProbeOutputDirectory Reserve(
        StageDragProbeOptions options,
        ProbePathEnvironment environment,
        Func<string, FileAttributes>? readAttributes = null)
    {
        ProbePathPolicy.Validate(options, environment, readAttributes);
        if (!ProbePathNative.CreateDirectory(options.OutputRoot, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                error is 80 or 183
                    ? $"The output root already exists: {options.OutputRoot}"
                    : $"Unable to create the output root: {options.OutputRoot}",
                new Win32Exception(error));
        }

        if (!Directory.Exists(options.OutputRoot))
        {
            throw new IOException(
                $"The output root was not observable after creation: {options.OutputRoot}");
        }

        return new ProbeOutputDirectory(options.OutputRoot);
    }
}

internal static class ProbePathNative
{
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectory(
        string path,
        nint securityAttributes);
}
