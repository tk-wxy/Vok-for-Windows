using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class StageDragProbePathTests
{
    [Fact]
    public void PrepareAndReceiveOptionsAreStrictAndDoNotCreateOutput()
    {
        using var layout = TestLayout.Create();
        var prepare = StageDragProbeOptions.Parse(
        [
            "prepare",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", layout.OutputRoot,
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--profile", "mixed",
        ]);
        var receive = StageDragProbeOptions.Parse(
        [
            "receive",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", layout.OutputRoot,
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--effect", "copy",
        ]);

        Assert.Equal(StageDragProbeVerb.Prepare, prepare.Verb);
        Assert.Equal(ProbeFixtureProfile.Mixed, prepare.Profile);
        Assert.Null(prepare.Effect);
        Assert.Equal(StageDragProbeVerb.Receive, receive.Verb);
        Assert.Equal(ProbeReceiverEffect.Copy, receive.Effect);
        Assert.Null(receive.Profile);
        Assert.False(Directory.Exists(layout.OutputRoot));

        Assert.Throws<ArgumentException>(() => StageDragProbeOptions.Parse(
        [
            "prepare",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", layout.OutputRoot,
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--profile", "mixed",
            "--effect", "copy",
        ]));
        Assert.Throws<ArgumentException>(() => StageDragProbeOptions.Parse(
        [
            "prepare",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", layout.OutputRoot,
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--profile", "mixed",
            "--profile", "single",
        ]));
        Assert.Throws<ArgumentException>(() => StageDragProbeOptions.Parse(
        [
            "prepare",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", "relative-output",
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--profile", "mixed",
        ]));
        Assert.Throws<ArgumentException>(() => StageDragProbeOptions.Parse(
        [
            "prepare",
            "--allowed-root", layout.AllowedRoot,
            "--output-root", layout.OutputRoot,
            "--repository-root", layout.RepositoryRoot,
            "--source-revision", "test-revision",
            "--profile", "mixed",
            "--unknown", "value",
        ]));
        Assert.False(Directory.Exists(layout.OutputRoot));
    }

    [Fact]
    public void PolicyRejectsEscapeProtectedRootsAndExistingTargetsWithoutCreatingOutput()
    {
        using var layout = TestLayout.Create();
        var environment = layout.Environment;
        var safe = layout.CreatePrepareOptions();
        ProbePathPolicy.Validate(safe, environment);

        var nested = safe with
        {
            OutputRoot = Path.Combine(layout.AllowedRoot, "nested", "run"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            ProbePathPolicy.Validate(nested, environment));

        var escaped = safe with
        {
            OutputRoot = Path.Combine(layout.Root, "escaped"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            ProbePathPolicy.Validate(escaped, environment));

        var repositoryAllowed = Path.Combine(layout.RepositoryRoot, "probe-output");
        Directory.CreateDirectory(repositoryAllowed);
        var repositoryTarget = safe with
        {
            AllowedRoot = repositoryAllowed,
            OutputRoot = Path.Combine(repositoryAllowed, "run"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            ProbePathPolicy.Validate(repositoryTarget, environment));

        Directory.CreateDirectory(environment.ProductRoot);
        var productTarget = safe with
        {
            AllowedRoot = environment.ProductRoot,
            OutputRoot = Path.Combine(environment.ProductRoot, "run"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            ProbePathPolicy.Validate(productTarget, environment));

        Directory.CreateDirectory(environment.UserProfile);
        var profileTarget = safe with
        {
            AllowedRoot = environment.UserProfile,
            OutputRoot = Path.Combine(environment.UserProfile, "run"),
        };
        Assert.Throws<InvalidOperationException>(() =>
            ProbePathPolicy.Validate(profileTarget, environment));

        Directory.CreateDirectory(layout.OutputRoot);
        Assert.Throws<IOException>(() => ProbePathPolicy.Validate(safe, environment));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.OutputRoot));
    }

    [Fact]
    public void PolicyRejectsAReparsePointBeforeCreatingOutput()
    {
        using var layout = TestLayout.Create();
        var options = layout.CreatePrepareOptions();

        Assert.Throws<InvalidOperationException>(() => ProbePathPolicy.Validate(
            options,
            layout.Environment,
            path => string.Equals(
                    StageDragProbeOptions.NormalizePath(path),
                    layout.AllowedRoot,
                    StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path)));
        Assert.False(Directory.Exists(layout.OutputRoot));
    }

    [Fact]
    public void AtomicOutputReservationAllowsExactlyOneWinner()
    {
        using var layout = TestLayout.Create();
        var options = layout.CreatePrepareOptions();
        var successes = new ConcurrentBag<ProbeOutputDirectory>();
        var failures = new ConcurrentBag<Exception>();

        Parallel.For(0, 2, _ =>
        {
            try
            {
                successes.Add(ProbeOutputDirectory.Reserve(options, layout.Environment));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        });

        var output = Assert.Single(successes);
        Assert.Equal(layout.OutputRoot, output.Path);
        Assert.IsType<IOException>(Assert.Single(failures));
        Assert.True(Directory.Exists(layout.OutputRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(layout.OutputRoot));
    }

    [Fact]
    public void ReportsAndEventsAreCreateOnlyAndStayInsideReservedRoot()
    {
        using var layout = TestLayout.Create();
        var output = ProbeOutputDirectory.Reserve(
            layout.CreatePrepareOptions(),
            layout.Environment);
        foreach (var invalidName in new[] { ".", "..", " ", "..\\escape.json", "report.json:alternate" })
        {
            Assert.Throws<ArgumentException>(() => ProbeReportWriter.PublishNew(
                output, invalidName, new TestReport(1, "Invalid")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(output.Path));
        }

        var reportPath = ProbeReportWriter.WriteNew(
            output,
            "report.json",
            new TestReport(1, "Succeeded"));

        Assert.Throws<IOException>(() => ProbeReportWriter.WriteNew(
            output,
            "report.json",
            new TestReport(1, "Overwritten")));
        Assert.Throws<ArgumentException>(() => ProbeReportWriter.WriteNew(
            output,
            "..\\escape.json",
            new TestReport(1, "Escaped")));
        Assert.Throws<ArgumentException>(() => ProbeReportWriter.WriteNew(
            output,
            "report.json:alternate",
            new TestReport(1, "AlternateStream")));
        var report = ProbeReportWriter.Read<TestReport>(reportPath);
        Assert.Equal(new TestReport(1, "Succeeded"), report);

        using (var events = ProbeEventLog.Create(output))
        {
            events.Write("ProcessStarted", new { mode = "prepare" });
            events.Write("OutputReserved");
        }

        Assert.Throws<IOException>(() => ProbeEventLog.Create(output));
        var lines = File.ReadAllLines(Path.Combine(output.Path, "events.jsonl"));
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("ProcessStarted", first.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("OutputReserved", second.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("sequence").GetInt64());
    }

    private sealed record TestReport(int ReportVersion, string Status);

    private sealed class TestLayout : IDisposable
    {
        private TestLayout(
            string root,
            string repositoryRoot,
            string allowedRoot,
            string outputRoot,
            ProbePathEnvironment environment)
        {
            Root = root;
            RepositoryRoot = repositoryRoot;
            AllowedRoot = allowedRoot;
            OutputRoot = outputRoot;
            Environment = environment;
        }

        public string Root { get; }

        public string RepositoryRoot { get; }

        public string AllowedRoot { get; }

        public string OutputRoot { get; }

        public ProbePathEnvironment Environment { get; }

        public static TestLayout Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"vok-stage-drag-path-tests-{Guid.NewGuid():N}");
            var repositoryRoot = Path.Combine(root, "repository");
            var allowedRoot = Path.Combine(root, "allowed");
            var productRoot = Path.Combine(root, "protected-product");
            var userProfile = Path.Combine(root, "protected-profile");
            Directory.CreateDirectory(Path.Combine(repositoryRoot, "src"));
            File.WriteAllText(Path.Combine(repositoryRoot, "src", "Vok.sln"), "test");
            Directory.CreateDirectory(allowedRoot);
            return new TestLayout(
                root,
                StageDragProbeOptions.NormalizePath(repositoryRoot),
                StageDragProbeOptions.NormalizePath(allowedRoot),
                StageDragProbeOptions.NormalizePath(Path.Combine(allowedRoot, "run")),
                new ProbePathEnvironment(
                    StageDragProbeOptions.NormalizePath(productRoot),
                    StageDragProbeOptions.NormalizePath(userProfile)));
        }

        public StageDragProbeOptions CreatePrepareOptions() => new(
            StageDragProbeVerb.Prepare,
            AllowedRoot,
            OutputRoot,
            RepositoryRoot,
            "test-revision",
            ProbeFixtureProfile.Mixed,
            null);

        public void Dispose()
        {
            if (!ProbePathPolicy.IsSameOrDescendant(Root, Path.GetTempPath()))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete a test root outside the temporary directory: {Root}");
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
