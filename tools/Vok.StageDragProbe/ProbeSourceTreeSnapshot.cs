using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vok.StageDragProbe;

internal static class ProbeSourceTreeSnapshot
{
    internal static IReadOnlyList<ProbeSourceSnapshot> Capture(
        string fixtureRoot,
        IReadOnlyList<ProbeFixtureSource> sources,
        Func<string, FileAttributes>? readAttributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureRoot);
        ArgumentNullException.ThrowIfNull(sources);
        readAttributes ??= File.GetAttributes;
        EnsureNotReparse(fixtureRoot, readAttributes);

        var snapshots = new List<ProbeSourceSnapshot>(sources.Count);
        foreach (var source in sources)
        {
            var path = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                source.RelativePath);
            var kind = ProbeFixtureManifestStore.ParseKind(source.Kind);
            snapshots.Add(kind switch
            {
                ProbeFixtureSourceKind.File => CaptureFile(
                    source,
                    path,
                    readAttributes,
                    requireShellLink: false),
                ProbeFixtureSourceKind.ShellLink => CaptureFile(
                    source,
                    path,
                    readAttributes,
                    requireShellLink: true),
                ProbeFixtureSourceKind.Directory => CaptureDirectory(
                    source,
                    path,
                    readAttributes),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            });
        }

        return snapshots;
    }

    private static ProbeSourceSnapshot CaptureFile(
        ProbeFixtureSource source,
        string path,
        Func<string, FileAttributes> readAttributes,
        bool requireShellLink)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A fixture source file is missing.", path);
        }

        var attributes = readAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            (requireShellLink &&
             !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A fixture source file has an invalid type.");
        }

        var info = new FileInfo(path);
        return new ProbeSourceSnapshot(
            source.SourceId,
            source.Kind,
            source.RelativePath,
            source.TargetSourceId,
            (int)attributes,
            info.Length,
            1,
            info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            ComputeFileDigest(path));
    }

    private static ProbeSourceSnapshot CaptureDirectory(
        ProbeFixtureSource source,
        string path,
        Func<string, FileAttributes> readAttributes)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"A fixture source directory is missing: {path}");
        }

        var rootAttributes = readAttributes(path);
        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "A fixture directory source has an invalid type.");
        }

        var pending = new Stack<string>();
        var lines = new List<string>();
        long totalBytes = 0;
        var itemCount = 1;
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = readAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Fixture directories cannot contain a reparse point: {entry}");
                }

                var relative = Path.GetRelativePath(path, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var lastWrite = File.GetLastWriteTimeUtc(entry)
                    .ToString("O", CultureInfo.InvariantCulture);
                itemCount++;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    lines.Add(ProbeFixtureDigest.JoinCanonical(
                        "directory",
                        relative,
                        ((int)attributes).ToString(CultureInfo.InvariantCulture),
                        lastWrite));
                    pending.Push(entry);
                    continue;
                }

                var info = new FileInfo(entry);
                totalBytes = checked(totalBytes + info.Length);
                lines.Add(ProbeFixtureDigest.JoinCanonical(
                    "file",
                    relative,
                    ((int)attributes).ToString(CultureInfo.InvariantCulture),
                    info.Length.ToString(CultureInfo.InvariantCulture),
                    lastWrite,
                    ComputeFileDigest(entry)));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        var rootInfo = new DirectoryInfo(path);
        return new ProbeSourceSnapshot(
            source.SourceId,
            source.Kind,
            source.RelativePath,
            source.TargetSourceId,
            (int)rootAttributes,
            totalBytes,
            itemCount,
            rootInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            ProbeFixtureDigest.ComputeLines(lines));
    }

    private static void EnsureNotReparse(
        string path,
        Func<string, FileAttributes> readAttributes)
    {
        if (!Directory.Exists(path) ||
            (readAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The fixture root is missing or is a reparse point.");
        }
    }

    private static string ComputeFileDigest(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal static class ProbeFixtureDigest
{
    internal static string ComputeSourceTreeDigest(
        IReadOnlyList<ProbeSourceSnapshot> snapshots) =>
        ComputeLines(snapshots.Select(snapshot => JoinCanonical(
            snapshot.SourceId,
            snapshot.Kind,
            snapshot.RelativePath,
            snapshot.TargetSourceId ?? string.Empty,
            snapshot.Attributes.ToString(CultureInfo.InvariantCulture),
            snapshot.ByteLength.ToString(CultureInfo.InvariantCulture),
            snapshot.ItemCount.ToString(CultureInfo.InvariantCulture),
            snapshot.LastWriteUtc,
            snapshot.ContentDigest)));

    internal static string ComputeOccurrenceDigest(
        IReadOnlyList<ProbeFixtureOccurrence> occurrences) =>
        ComputeLines(occurrences.Select(occurrence => JoinCanonical(
            occurrence.Ordinal.ToString(CultureInfo.InvariantCulture),
            occurrence.SourceId)));

    internal static string ComputeLines(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    internal static string JoinCanonical(params string[] values) =>
        string.Join(
            '|',
            values.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));
}
