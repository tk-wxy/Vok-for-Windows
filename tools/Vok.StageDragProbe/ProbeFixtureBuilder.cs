using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Vok.StageDragProbe;

internal static class ProbeFixtureBuilder
{
    internal static ProbeFixtureBundle Build(
        StageDragProbeOptions options,
        ProbeOutputDirectory output,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Verb is not StageDragProbeVerb.Prepare || options.Profile is null)
        {
            throw new InvalidOperationException(
                "Only prepare options can build a fixture.");
        }

        if (!string.Equals(
                StageDragProbeOptions.NormalizePath(options.OutputRoot),
                StageDragProbeOptions.NormalizePath(output.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The reserved output root does not match the prepare request.");
        }

        runId ??= Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(runId, "N", out _) ||
            !string.Equals(runId, runId.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The fixture run id must be a lowercase N-format GUID.");
        }

        var fixtureRoot = Path.Combine(
            output.Path,
            ProbeFixtureManifestStore.FixtureDirectoryName);
        CreateDirectoryNew(fixtureRoot);
        var definition = CreateDefinition(options.Profile.Value, runId);
        Materialize(fixtureRoot, definition.Sources, runId);

        var snapshots = ProbeSourceTreeSnapshot.Capture(
            fixtureRoot,
            definition.Sources);
        var sourceTreeDigest = ProbeFixtureDigest.ComputeSourceTreeDigest(snapshots);
        var occurrenceDigest = ProbeFixtureDigest.ComputeOccurrenceDigest(
            definition.Occurrences);
        var sourceDocument = new ProbeSourceSnapshotDocument(
            ProbeFixtureManifestStore.CurrentSourceVersion,
            runId,
            snapshots,
            sourceTreeDigest);
        var manifest = new ProbeFixtureManifest(
            ProbeFixtureManifestStore.CurrentManifestVersion,
            runId,
            ProbeFixtureManifestStore.ProfileToWireValue(options.Profile.Value),
            options.SourceRevision,
            definition.Sources,
            definition.Occurrences,
            occurrenceDigest,
            sourceTreeDigest);

        ProbeReportWriter.WriteNew(
            output,
            ProbeFixtureManifestStore.SourceFileName,
            sourceDocument);
        ProbeReportWriter.WriteNew(
            output,
            ProbeFixtureManifestStore.ManifestFileName,
            manifest);
        return ProbeFixtureManifestStore.LoadAndValidate(
            output,
            options.Profile.Value,
            options.SourceRevision);
    }

    private static FixtureDefinition CreateDefinition(
        ProbeFixtureProfile profile,
        string runId) =>
        profile switch
        {
            ProbeFixtureProfile.Single => CreateFileBatch("single", 1, runId),
            ProbeFixtureProfile.Batch10 => CreateFileBatch("batch", 10, runId),
            ProbeFixtureProfile.Batch100 => CreateFileBatch("batch", 100, runId),
            ProbeFixtureProfile.Mixed => CreateMixed(runId),
            ProbeFixtureProfile.Duplicate => CreateDuplicate(runId),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };

    private static FixtureDefinition CreateFileBatch(
        string prefix,
        int count,
        string runId)
    {
        var sources = Enumerable.Range(0, count)
            .Select(index => new ProbeFixtureSource(
                SourceId(index),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.File),
                $"{prefix}-{index:D3}-{runId}.txt",
                null))
            .ToArray();
        return new FixtureDefinition(
            sources,
            sources.Select((source, index) =>
                    new ProbeFixtureOccurrence(index, source.SourceId))
                .ToArray());
    }

    private static FixtureDefinition CreateMixed(string runId)
    {
        ProbeFixtureSource[] sources =
        [
            new(
                SourceId(0),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.File),
                $"ascii-{runId}.txt",
                null),
            new(
                SourceId(1),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.File),
                $"unicode-示例-{runId}.txt",
                null),
            new(
                SourceId(2),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.Directory),
                $"directory-{runId}",
                null),
            new(
                SourceId(3),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.ShellLink),
                $"shortcut-{runId}.lnk",
                SourceId(0)),
        ];
        return new FixtureDefinition(
            sources,
            sources.Select((source, index) =>
                    new ProbeFixtureOccurrence(index, source.SourceId))
                .ToArray());
    }

    private static FixtureDefinition CreateDuplicate(string runId)
    {
        var sources = new[] { "a", "b", "c" }
            .Select((name, index) => new ProbeFixtureSource(
                SourceId(index),
                ProbeFixtureManifestStore.KindToWireValue(ProbeFixtureSourceKind.File),
                $"duplicate-{name}-{runId}.txt",
                null))
            .ToArray();
        int[] order = [0, 1, 0, 2, 0, 1];
        return new FixtureDefinition(
            sources,
            order.Select((sourceIndex, ordinal) =>
                    new ProbeFixtureOccurrence(ordinal, SourceId(sourceIndex)))
                .ToArray());
    }

    private static void Materialize(
        string fixtureRoot,
        IReadOnlyList<ProbeFixtureSource> sources,
        string runId)
    {
        var byId = sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        foreach (var source in sources.Where(
                     source => ProbeFixtureManifestStore.ParseKind(source.Kind) is not
                         ProbeFixtureSourceKind.ShellLink))
        {
            var path = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                source.RelativePath);
            if (ProbeFixtureManifestStore.ParseKind(source.Kind) is
                ProbeFixtureSourceKind.Directory)
            {
                CreateDirectoryNew(path);
                WriteFileNew(
                    Path.Combine(path, $"nested-{runId}.txt"),
                    $"Vok Stage drag probe directory fixture {runId}\n");
            }
            else
            {
                WriteFileNew(
                    path,
                    $"Vok Stage drag probe source {source.SourceId} {runId}\n");
            }
        }

        foreach (var source in sources.Where(
                     source => ProbeFixtureManifestStore.ParseKind(source.Kind) is
                         ProbeFixtureSourceKind.ShellLink))
        {
            var path = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                source.RelativePath);
            var target = byId[source.TargetSourceId!];
            var targetPath = ProbeFixtureRelativePath.ResolveDirectChild(
                fixtureRoot,
                target.RelativePath);
            ProbeShellLink.CreateAndVerify(
                path,
                targetPath,
                $"Vok Stage drag probe {runId}");
        }
    }

    private static void CreateDirectoryNew(string path)
    {
        if (!ProbePathNative.CreateDirectory(path, 0))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                error is 80 or 183
                    ? $"The fixture directory already exists: {path}"
                    : $"Unable to create the fixture directory: {path}",
                new Win32Exception(error));
        }
    }

    private static void WriteFileNew(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(content);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string SourceId(int index) => $"source-{index:D3}";

    private sealed record FixtureDefinition(
        IReadOnlyList<ProbeFixtureSource> Sources,
        IReadOnlyList<ProbeFixtureOccurrence> Occurrences);
}
