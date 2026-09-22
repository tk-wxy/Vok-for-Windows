using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageDragProbeFixtureTests
{
    [Theory]
    [InlineData("single", 1, 1)]
    [InlineData("batch10", 10, 10)]
    [InlineData("batch100", 100, 100)]
    [InlineData("mixed", 4, 4)]
    [InlineData("duplicate", 3, 6)]
    public void EveryProfileBuildsAndReloadsWithStableDigests(
        string profileName,
        int expectedSourceCount,
        int expectedOccurrenceCount)
    {
        var profile = ParseProfile(profileName);
        using var layout = TestLayout.Create(profile);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            var built = ProbeFixtureBuilder.Build(layout.Options, output);
            var reloaded = ProbeFixtureManifestStore.LoadAndValidate(
                output,
                profile,
                layout.Options.SourceRevision);

            Assert.Equal(expectedSourceCount, built.Manifest.Sources.Count);
            Assert.Equal(expectedOccurrenceCount, built.Manifest.Occurrences.Count);
            Assert.Equal(expectedOccurrenceCount, built.OrderedPaths.Count);
            Assert.Equal(
                built.Manifest.SourceTreeDigest,
                reloaded.Manifest.SourceTreeDigest);
            Assert.Equal(
                built.Manifest.OrderedOccurrenceDigest,
                reloaded.Manifest.OrderedOccurrenceDigest);
            Assert.Equal(
                built.SourceSnapshot.Sources.ToArray(),
                reloaded.SourceSnapshot.Sources.ToArray());
            Assert.All(
                built.Manifest.Sources,
                source => Assert.Contains(
                    built.Manifest.RunId,
                    source.RelativePath,
                    StringComparison.Ordinal));
            Assert.All(
                built.OrderedPaths,
                path => Assert.True(
                    ProbePathPolicy.IsSameOrDescendant(path, built.FixtureRoot)));

            var manifestText = File.ReadAllText(
                Path.Combine(output.Path, ProbeFixtureManifestStore.ManifestFileName));
            var sourceText = File.ReadAllText(
                Path.Combine(output.Path, ProbeFixtureManifestStore.SourceFileName));
            Assert.DoesNotContain(layout.Root, manifestText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(layout.Root, sourceText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void MixedFixtureContainsUnicodeDirectoryAndValidProbeOwnedShellLink()
    {
        using var layout = TestLayout.Create(ProbeFixtureProfile.Mixed);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            var bundle = ProbeFixtureBuilder.Build(layout.Options, output);
            var link = Assert.Single(
                bundle.Manifest.Sources,
                source => source.Kind == "shellLink");
            var directory = Assert.Single(
                bundle.Manifest.Sources,
                source => source.Kind == "directory");
            Assert.Contains(
                bundle.Manifest.Sources,
                source => source.RelativePath.Any(character => character > 127));

            var target = Assert.Single(
                bundle.Manifest.Sources,
                source => source.SourceId == link.TargetSourceId);
            var linkPath = Path.Combine(bundle.FixtureRoot, link.RelativePath);
            var expectedTarget = Path.Combine(bundle.FixtureRoot, target.RelativePath);
            var actualTarget = ProbeShellLink.ReadTarget(linkPath);
            Assert.Equal(
                StageDragProbeOptions.NormalizePath(expectedTarget),
                StageDragProbeOptions.NormalizePath(actualTarget),
                ignoreCase: true);
            Assert.True(Directory.Exists(
                Path.Combine(bundle.FixtureRoot, directory.RelativePath)));
            Assert.True(new FileInfo(linkPath).Length > 0);
        });
    }

    [Fact]
    public void DuplicateFixturePreservesNonAdjacentOccurrencesWithoutDuplicatingSources()
    {
        using var layout = TestLayout.Create(ProbeFixtureProfile.Duplicate);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            var bundle = ProbeFixtureBuilder.Build(layout.Options, output);

            Assert.Equal(3, bundle.Manifest.Sources.Count);
            Assert.Equal(
                new[]
                {
                    "source-000",
                    "source-001",
                    "source-000",
                    "source-002",
                    "source-000",
                    "source-001",
                },
                bundle.Manifest.Occurrences
                    .Select(occurrence => occurrence.SourceId)
                    .ToArray());
            Assert.Equal(3, Directory.EnumerateFiles(bundle.FixtureRoot).Count());
            Assert.Equal(bundle.OrderedPaths[0], bundle.OrderedPaths[2]);
            Assert.Equal(bundle.OrderedPaths[0], bundle.OrderedPaths[4]);
        });
    }

    [Fact]
    public void LoaderRejectsEscapingAndUnknownManifestReferences()
    {
        using var layout = TestLayout.Create(ProbeFixtureProfile.Single);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            _ = ProbeFixtureBuilder.Build(layout.Options, output);
            var manifestPath = Path.Combine(
                output.Path,
                ProbeFixtureManifestStore.ManifestFileName);
            var original = File.ReadAllText(manifestPath);

            var escaping = JsonNode.Parse(original)!.AsObject();
            escaping["sources"]!.AsArray()[0]!["relativePath"] = "../escape.txt";
            File.WriteAllText(manifestPath, escaping.ToJsonString());
            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Single,
                    layout.Options.SourceRevision));

            File.WriteAllText(manifestPath, original);
            var unknown = JsonNode.Parse(original)!.AsObject();
            unknown["occurrences"]!.AsArray()[0]!["sourceId"] = "source-999";
            File.WriteAllText(manifestPath, unknown.ToJsonString());
            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Single,
                    layout.Options.SourceRevision));
        });
    }

    [Fact]
    public void LoaderRejectsSourceMutationMissingItemsAndUntrackedItems()
    {
        using var layout = TestLayout.Create(ProbeFixtureProfile.Mixed);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            var bundle = ProbeFixtureBuilder.Build(layout.Options, output);
            var directory = Assert.Single(
                bundle.Manifest.Sources,
                source => source.Kind == "directory");
            var nested = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(bundle.FixtureRoot, directory.RelativePath)));
            File.AppendAllText(nested, "tampered");
            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Mixed,
                    layout.Options.SourceRevision));
        });

        using var missingLayout = TestLayout.Create(ProbeFixtureProfile.Single);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                missingLayout.Options,
                missingLayout.Environment);
            var bundle = ProbeFixtureBuilder.Build(missingLayout.Options, output);
            File.Delete(bundle.OrderedPaths[0]);
            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Single,
                    missingLayout.Options.SourceRevision));
        });

        using var untrackedLayout = TestLayout.Create(ProbeFixtureProfile.Single);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                untrackedLayout.Options,
                untrackedLayout.Environment);
            var bundle = ProbeFixtureBuilder.Build(untrackedLayout.Options, output);
            File.WriteAllText(Path.Combine(bundle.FixtureRoot, "untracked.txt"), "unexpected");
            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Single,
                    untrackedLayout.Options.SourceRevision));
        });
    }

    [Fact]
    public void LoaderRejectsAReparseSourceBeforeReadingIt()
    {
        using var layout = TestLayout.Create(ProbeFixtureProfile.Single);
        RunOnSta(() =>
        {
            var output = ProbeOutputDirectory.Reserve(
                layout.Options,
                layout.Environment);
            var bundle = ProbeFixtureBuilder.Build(layout.Options, output);
            var sourcePath = bundle.OrderedPaths[0];

            Assert.Throws<InvalidDataException>(() =>
                ProbeFixtureManifestStore.LoadAndValidate(
                    output,
                    ProbeFixtureProfile.Single,
                    layout.Options.SourceRevision,
                    path => string.Equals(
                            StageDragProbeOptions.NormalizePath(path),
                            StageDragProbeOptions.NormalizePath(sourcePath),
                            StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.Archive | FileAttributes.ReparsePoint
                        : File.GetAttributes(path)));
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Vok Stage drag fixture test STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static ProbeFixtureProfile ParseProfile(string value) =>
        value switch
        {
            "single" => ProbeFixtureProfile.Single,
            "batch10" => ProbeFixtureProfile.Batch10,
            "batch100" => ProbeFixtureProfile.Batch100,
            "mixed" => ProbeFixtureProfile.Mixed,
            "duplicate" => ProbeFixtureProfile.Duplicate,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private sealed class TestLayout : IDisposable
    {
        private TestLayout(
            string root,
            StageDragProbeOptions options,
            ProbePathEnvironment environment)
        {
            Root = root;
            Options = options;
            Environment = environment;
        }

        public string Root { get; }

        public StageDragProbeOptions Options { get; }

        public ProbePathEnvironment Environment { get; }

        public static TestLayout Create(ProbeFixtureProfile profile)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"vok-stage-drag-fixture-tests-{Guid.NewGuid():N}");
            var repositoryRoot = Path.Combine(root, "repository");
            var allowedRoot = Path.Combine(root, "allowed");
            Directory.CreateDirectory(Path.Combine(repositoryRoot, "src"));
            File.WriteAllText(Path.Combine(repositoryRoot, "src", "Vok.sln"), "test");
            Directory.CreateDirectory(allowedRoot);
            var outputRoot = Path.Combine(allowedRoot, "run");
            return new TestLayout(
                root,
                new StageDragProbeOptions(
                    StageDragProbeVerb.Prepare,
                    StageDragProbeOptions.NormalizePath(allowedRoot),
                    StageDragProbeOptions.NormalizePath(outputRoot),
                    StageDragProbeOptions.NormalizePath(repositoryRoot),
                    "fixture-test-revision",
                    profile,
                    null),
                new ProbePathEnvironment(
                    StageDragProbeOptions.NormalizePath(
                        Path.Combine(root, "protected-product")),
                    StageDragProbeOptions.NormalizePath(
                        Path.Combine(root, "protected-profile"))));
        }

        public void Dispose()
        {
            if (!ProbePathPolicy.IsSameOrDescendant(Root, Path.GetTempPath()))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete a fixture test root outside temp: {Root}");
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
