using System.Globalization;

namespace Vok.StageDragProbe;

internal enum ProbeFixtureSourceKind
{
    File,
    Directory,
    ShellLink,
}

internal sealed record ProbeFixtureSource(
    string SourceId,
    string Kind,
    string RelativePath,
    string? TargetSourceId);

internal sealed record ProbeFixtureOccurrence(
    int Ordinal,
    string SourceId);

internal sealed record ProbeFixtureManifest(
    int ManifestVersion,
    string RunId,
    string Profile,
    string SourceRevision,
    IReadOnlyList<ProbeFixtureSource> Sources,
    IReadOnlyList<ProbeFixtureOccurrence> Occurrences,
    string OrderedOccurrenceDigest,
    string SourceTreeDigest);

internal sealed record ProbeSourceSnapshot(
    string SourceId,
    string Kind,
    string RelativePath,
    string? TargetSourceId,
    int Attributes,
    long ByteLength,
    int ItemCount,
    string LastWriteUtc,
    string ContentDigest);

internal sealed record ProbeSourceSnapshotDocument(
    int SourceVersion,
    string RunId,
    IReadOnlyList<ProbeSourceSnapshot> Sources,
    string SourceTreeDigest);

internal sealed record ProbeFixtureBundle(
    ProbeFixtureManifest Manifest,
    ProbeSourceSnapshotDocument SourceSnapshot,
    string FixtureRoot,
    IReadOnlyList<string> OrderedPaths);

internal static class ProbeFixtureManifestStore
{
    internal const int CurrentManifestVersion = 1;
    internal const int CurrentSourceVersion = 1;
    internal const string FixtureDirectoryName = "fixture";
    internal const string ManifestFileName = "manifest.json";
    internal const string SourceFileName = "source.json";

    internal static ProbeFixtureBundle LoadAndValidate(
        ProbeOutputDirectory output,
        ProbeFixtureProfile expectedProfile,
        string expectedSourceRevision,
        Func<string, FileAttributes>? readAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceRevision);
        var fixtureRoot = Path.Combine(output.Path, FixtureDirectoryName);
        if (!Directory.Exists(fixtureRoot))
        {
            throw new DirectoryNotFoundException(
                $"The fixture root does not exist: {fixtureRoot}");
        }

        var manifest = ProbeReportWriter.Read<ProbeFixtureManifest>(
            Path.Combine(output.Path, ManifestFileName));
        var sourceDocument = ProbeReportWriter.Read<ProbeSourceSnapshotDocument>(
            Path.Combine(output.Path, SourceFileName));
        ValidateHeader(manifest, sourceDocument, expectedProfile, expectedSourceRevision);
        ValidateDefinitions(manifest, fixtureRoot, expectedProfile);

        var actualSnapshots = ProbeSourceTreeSnapshot.Capture(
            fixtureRoot,
            manifest.Sources,
            readAttributes);
        ValidateSnapshotDocument(manifest, sourceDocument, actualSnapshots);

        var occurrenceDigest = ProbeFixtureDigest.ComputeOccurrenceDigest(
            manifest.Occurrences);
        if (!string.Equals(
                occurrenceDigest,
                manifest.OrderedOccurrenceDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The ordered occurrence digest is invalid.");
        }

        var sourcesById = manifest.Sources.ToDictionary(
            source => source.SourceId,
            StringComparer.Ordinal);
        var orderedPaths = manifest.Occurrences
            .Select(occurrence => ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                sourcesById[occurrence.SourceId].RelativePath))
            .ToArray();
        ValidateShellLinks(fixtureRoot, manifest.Sources, sourcesById);
        return new ProbeFixtureBundle(
            manifest,
            sourceDocument,
            fixtureRoot,
            orderedPaths);
    }

    internal static string ProfileToWireValue(ProbeFixtureProfile profile) =>
        profile switch
        {
            ProbeFixtureProfile.Single => "single",
            ProbeFixtureProfile.Batch10 => "batch10",
            ProbeFixtureProfile.Batch100 => "batch100",
            ProbeFixtureProfile.Mixed => "mixed",
            ProbeFixtureProfile.Duplicate => "duplicate",
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

    internal static string KindToWireValue(ProbeFixtureSourceKind kind) =>
        kind switch
        {
            ProbeFixtureSourceKind.File => "file",
            ProbeFixtureSourceKind.Directory => "directory",
            ProbeFixtureSourceKind.ShellLink => "shellLink",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    internal static ProbeFixtureSourceKind ParseKind(string value) =>
        value switch
        {
            "file" => ProbeFixtureSourceKind.File,
            "directory" => ProbeFixtureSourceKind.Directory,
            "shellLink" => ProbeFixtureSourceKind.ShellLink,
            _ => throw new InvalidDataException($"Unknown fixture source kind: {value}"),
        };

    private static void ValidateHeader(
        ProbeFixtureManifest manifest,
        ProbeSourceSnapshotDocument sourceDocument,
        ProbeFixtureProfile expectedProfile,
        string expectedSourceRevision)
    {
        if (manifest.ManifestVersion != CurrentManifestVersion)
        {
            throw new InvalidDataException(
                $"Unsupported fixture manifest version: {manifest.ManifestVersion}");
        }

        if (sourceDocument.SourceVersion != CurrentSourceVersion)
        {
            throw new InvalidDataException(
                $"Unsupported source snapshot version: {sourceDocument.SourceVersion}");
        }

        if (!Guid.TryParseExact(manifest.RunId, "N", out _) ||
            !string.Equals(manifest.RunId, manifest.RunId.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fixture run id is invalid.");
        }

        if (!string.Equals(manifest.RunId, sourceDocument.RunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The manifest and source snapshot run ids differ.");
        }

        if (!string.Equals(
                manifest.Profile,
                ProfileToWireValue(expectedProfile),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fixture profile does not match the request.");
        }

        if (!string.Equals(
                manifest.SourceRevision,
                expectedSourceRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fixture source revision does not match the request.");
        }
    }

    private static void ValidateDefinitions(
        ProbeFixtureManifest manifest,
        string fixtureRoot,
        ProbeFixtureProfile profile)
    {
        if (manifest.Sources.Count == 0 || manifest.Occurrences.Count == 0)
        {
            throw new InvalidDataException("The fixture manifest is empty.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < manifest.Sources.Count; index++)
        {
            var source = manifest.Sources[index];
            var expectedId = "source-" + index.ToString("D3", CultureInfo.InvariantCulture);
            if (!string.Equals(source.SourceId, expectedId, StringComparison.Ordinal) ||
                !sourceIds.Add(source.SourceId))
            {
                throw new InvalidDataException("Fixture source ids are invalid or duplicated.");
            }

            _ = ParseKind(source.Kind);
            _ = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                source.RelativePath);
            if (!relativePaths.Add(source.RelativePath) ||
                !source.RelativePath.Contains(manifest.RunId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Fixture source paths must be unique and include the run id.");
            }
        }

        for (var index = 0; index < manifest.Occurrences.Count; index++)
        {
            var occurrence = manifest.Occurrences[index];
            if (occurrence.Ordinal != index || !sourceIds.Contains(occurrence.SourceId))
            {
                throw new InvalidDataException(
                    "Fixture occurrences must be contiguous and reference known sources.");
            }
        }

        ValidateTargets(manifest.Sources);
        ValidateProfileShape(manifest, profile);
        ValidateNoUntrackedRootEntries(fixtureRoot, manifest.Sources);
    }

    private static void ValidateTargets(IReadOnlyList<ProbeFixtureSource> sources)
    {
        var byId = sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var kind = ParseKind(source.Kind);
            if (kind is not ProbeFixtureSourceKind.ShellLink)
            {
                if (source.TargetSourceId is not null)
                {
                    throw new InvalidDataException(
                        "Only a Shell link source may declare a target source.");
                }

                continue;
            }

            if (!source.RelativePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                source.TargetSourceId is null ||
                !byId.TryGetValue(source.TargetSourceId, out var target) ||
                ParseKind(target.Kind) is not ProbeFixtureSourceKind.File)
            {
                throw new InvalidDataException(
                    "A Shell link must target a declared fixture file.");
            }
        }
    }

    private static void ValidateProfileShape(
        ProbeFixtureManifest manifest,
        ProbeFixtureProfile profile)
    {
        var expectedSourceCount = profile switch
        {
            ProbeFixtureProfile.Single => 1,
            ProbeFixtureProfile.Batch10 => 10,
            ProbeFixtureProfile.Batch100 => 100,
            ProbeFixtureProfile.Mixed => 4,
            ProbeFixtureProfile.Duplicate => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        var expectedOccurrenceCount = profile is ProbeFixtureProfile.Duplicate
            ? 6
            : expectedSourceCount;
        if (manifest.Sources.Count != expectedSourceCount ||
            manifest.Occurrences.Count != expectedOccurrenceCount)
        {
            throw new InvalidDataException("The fixture profile item count is invalid.");
        }

        var occurrenceIds = manifest.Occurrences
            .Select(occurrence => occurrence.SourceId)
            .ToArray();
        if (profile is ProbeFixtureProfile.Duplicate)
        {
            string[] expected =
            [
                "source-000",
                "source-001",
                "source-000",
                "source-002",
                "source-000",
                "source-001",
            ];
            if (!occurrenceIds.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The duplicate occurrence order is invalid.");
            }
        }
        else if (!occurrenceIds.SequenceEqual(
                     manifest.Sources.Select(source => source.SourceId),
                     StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "A non-duplicate profile must contain each source exactly once in source order.");
        }

        if (profile is ProbeFixtureProfile.Mixed)
        {
            var kinds = manifest.Sources
                .Select(source => ParseKind(source.Kind))
                .ToArray();
            if (kinds.Count(kind => kind is ProbeFixtureSourceKind.File) != 2 ||
                kinds.Count(kind => kind is ProbeFixtureSourceKind.Directory) != 1 ||
                kinds.Count(kind => kind is ProbeFixtureSourceKind.ShellLink) != 1 ||
                !manifest.Sources.Any(source => source.RelativePath.Any(character => character > 127)))
            {
                throw new InvalidDataException("The mixed fixture shape is invalid.");
            }
        }
    }

    private static void ValidateNoUntrackedRootEntries(
        string fixtureRoot,
        IReadOnlyList<ProbeFixtureSource> sources)
    {
        var expected = sources
            .Select(source => source.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actual = Directory.EnumerateFileSystemEntries(fixtureRoot)
            .Select(path => Path.GetFileName(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The fixture root contains missing or untracked source entries.");
        }
    }

    private static void ValidateSnapshotDocument(
        ProbeFixtureManifest manifest,
        ProbeSourceSnapshotDocument sourceDocument,
        IReadOnlyList<ProbeSourceSnapshot> actualSnapshots)
    {
        if (sourceDocument.Sources.Count != actualSnapshots.Count)
        {
            throw new InvalidDataException("The source snapshot count is invalid.");
        }

        for (var index = 0; index < actualSnapshots.Count; index++)
        {
            if (sourceDocument.Sources[index] != actualSnapshots[index])
            {
                throw new InvalidDataException(
                    $"The fixture source snapshot changed: {actualSnapshots[index].SourceId}");
            }
        }

        var sourceTreeDigest = ProbeFixtureDigest.ComputeSourceTreeDigest(actualSnapshots);
        if (!string.Equals(
                sourceTreeDigest,
                sourceDocument.SourceTreeDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceTreeDigest,
                manifest.SourceTreeDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The fixture source tree digest is invalid.");
        }
    }

    private static void ValidateShellLinks(
        string fixtureRoot,
        IReadOnlyList<ProbeFixtureSource> sources,
        IReadOnlyDictionary<string, ProbeFixtureSource> sourcesById)
    {
        foreach (var source in sources.Where(
                     source => ParseKind(source.Kind) is ProbeFixtureSourceKind.ShellLink))
        {
            var linkPath = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                source.RelativePath);
            var targetSource = sourcesById[source.TargetSourceId!];
            var expectedTarget = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                targetSource.RelativePath);
            var actualTarget = ProbeShellLink.ReadTarget(linkPath);
            if (!string.Equals(
                    StageDragProbeOptions.NormalizePath(actualTarget),
                    StageDragProbeOptions.NormalizePath(expectedTarget),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The Shell link target is outside or differs from its fixture target.");
            }
        }
    }
}

internal static class ProbeFixtureRelativePath
{
    internal static string ResolveDirectChild(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Fixture paths must be normalized relative paths.");
        }

        var segments = relativePath.Split('/');
        if (segments.Length != 1 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException(
                "Fixture source paths must be one safe path segment.");
        }

        var normalizedRoot = StageDragProbeOptions.NormalizePath(root);
        var candidate = StageDragProbeOptions.NormalizePath(
            Path.Combine(normalizedRoot, relativePath));
        if (!ProbePathPolicy.IsSameOrDescendant(candidate, normalizedRoot) ||
            string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A fixture path escaped its root.");
        }

        var roundTrip = Path.GetRelativePath(normalizedRoot, candidate)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(roundTrip, relativePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A fixture path is not in canonical form.");
        }

        return candidate;
    }
}
