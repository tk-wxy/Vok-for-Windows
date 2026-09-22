using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed partial class StageDragProbeProcessTests(WpfApplicationFixture wpf)
{
    [Fact]
    public async Task ActualExecutablePreparesMixedWithMatchingIdentityAndNoProductDependencies()
    {
        using var layout = new TestLayout();
        await using var process = layout.Start("prepare", "mixed");
        Assert.Equal(0, await process.WaitForExitAsync());
        Assert.Empty(await process.StandardError);
        var identity = layout.Read<ProbeProcessIdentity>("startup.json");
        AssertIdentity(layout, process, identity);
        var report = layout.Read<ProbePrepareReport>("report.json");
        Assert.Equal(1, report.ReportVersion);
        Assert.Equal("FixturePreparation", report.EvidenceScope);
        Assert.Equal("Prepared", report.Status);
        Assert.Equal("mixed", report.Profile);
        Assert.Equal(4, report.SourceCount);
        Assert.Equal(4, report.OccurrenceCount);
        Assert.Equal(identity.RunId, report.RunId);
        wpf.Invoke(() =>
        {
            var bundle = ProbeFixtureManifestStore.LoadAndValidate(
                layout.Output, ProbeFixtureProfile.Mixed, TestLayout.Revision);
            Assert.Equal(identity.RunId, bundle.Manifest.RunId);
            Assert.Equal(report.SourceTreeDigest, bundle.Manifest.SourceTreeDigest);
            Assert.Equal(report.OrderedOccurrenceDigest, bundle.Manifest.OrderedOccurrenceDigest);
        });
        var exit = layout.Read<ProbeProcessExitReport>("exit.json");
        Assert.Equal(0, exit.ExitCode);
        Assert.Null(exit.Error);
        Assert.Null(exit.EvidenceError);
        Assert.Equal(identity.RunId, exit.RunId);
        Assert.False(File.Exists(Path.Combine(layout.Output.Path, "failure.json")));
        Assert.Empty(Directory.EnumerateFiles(layout.Output.Path, "*.pending"));
        Assert.Contains(layout.ReadEvents(), entry => entry.Name == "FixtureCreated");
        Assert.Single(layout.ReadEvents(), entry => entry.Name == "PrepareTerminal");

        Assert.Equal(4, Directory.EnumerateFiles(layout.BinaryRoot).Count());
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(layout.BinaryRoot, "Vok.StageDragProbe.deps.json")));
        Assert.All(deps.RootElement.GetProperty("libraries").EnumerateObject(),
            library => Assert.StartsWith("Vok.StageDragProbe/", library.Name));
        Assert.DoesNotContain(typeof(ProbeProcessIdentity).Assembly.GetReferencedAssemblies(),
            name => name.Name!.StartsWith("Vok.", StringComparison.Ordinal) || name.Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Copy")]
    [InlineData("None")]
    [InlineData("Throw")]
    public async Task ActualReceiverShowsAndClosesWithoutOleWithFailedExitAndOneReport(string policy)
    {
        using var layout = new TestLayout();
        await using var process = layout.Start("receive", policy);
        await process.WaitForEventAsync("ReceiverShown");
        var identity = layout.Read<ProbeProcessIdentity>("startup.json");
        AssertIdentity(layout, process, identity);
        var ready = Assert.Single(layout.ReadEvents(), entry => entry.Name == "ReceiverReady");
        var handle = (nint)ready.Details.GetProperty("windowHandle").GetInt64();
        Assert.NotEqual(nint.Zero, handle);
        Assert.True(NativeMethods.IsWindowVisible(handle));
        NativeMethods.GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal(process.Id, owner);
        Assert.True(NativeMethods.PostMessage(handle, 0x0010, 0, 0)); // WM_CLOSE, fixed PID/HWND only.
        Assert.Equal(1, await process.WaitForExitAsync());
        var report = layout.Read<ProbeReceiverReport>("failure.json");
        Assert.Equal(identity.RunId, report.RunId);
        Assert.Equal(1, report.ReportVersion);
        Assert.Equal("ReceiverObservation", report.EvidenceScope);
        Assert.Equal("WindowClosedBeforeDrop", report.Reason);
        Assert.Equal(policy, report.Policy);
        Assert.Equal(0, report.Generation);
        Assert.Equal(0, report.DropCount);
        Assert.Equal(0, report.FileDropReadCount);
        Assert.Equal(0, report.RawEffect);
        Assert.Empty(report.Paths);
        Assert.False(NativeMethods.IsWindow(handle));
        Assert.Single(layout.ReadEvents(), entry => entry.Name == "ReceiverTerminal");
        Assert.False(File.Exists(Path.Combine(layout.Output.Path, "report.json")));
        var exit = layout.Read<ProbeProcessExitReport>("exit.json");
        Assert.Equal(1, exit.ExitCode);
        Assert.Equal("Failed", exit.Status);
        Assert.Equal(identity.RunId, exit.RunId);
    }

    [Theory]
    [InlineData("invalid-profile")]
    [InlineData("unknown-option")]
    [InlineData("existing-output")]
    public async Task ActualExecutableRejectsBeforeCreatingOrChangingOutput(string invalid)
    {
        using var layout = new TestLayout();
        if (invalid == "existing-output")
        {
            Directory.CreateDirectory(layout.Output.Path);
            File.WriteAllText(Path.Combine(layout.Output.Path, "sentinel.txt"), "old evidence");
        }
        await using var process = layout.Start("prepare", invalid == "invalid-profile" ? "bad" : "mixed",
            invalid == "unknown-option" ? ["--unknown", "value"] : []);
        Assert.Equal(1, await process.WaitForExitAsync());
        Assert.NotEmpty(await process.StandardError);
        if (invalid == "existing-output")
        {
            Assert.Equal("old evidence", File.ReadAllText(Path.Combine(layout.Output.Path, "sentinel.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(layout.Output.Path));
        }
        else Assert.False(Directory.Exists(layout.Output.Path));
    }

    [Fact]
    public async Task ExternalWatchdogKillsOnlyItsOwnedReceiverAndPreservesFlushedEvents()
    {
        using var layout = new TestLayout();
        await using var process = layout.Start("receive", "Copy");
        await process.WaitForEventAsync("ReceiverShown");
        var before = layout.ReadEvents();
        // Deliberate no-Drop deadline injection, not a product SLA or success barrier.
        await Assert.ThrowsAsync<TimeoutException>(() => process.WaitForExitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(process.HasExited);
        using var watchdog = JsonDocument.Parse(File.ReadAllText(layout.WatchdogPath));
        Assert.Equal("Failed", watchdog.RootElement.GetProperty("status").GetString());
        Assert.Equal(process.Id, watchdog.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal("ExternalWatchdog", watchdog.RootElement.GetProperty("evidenceScope").GetString());
        Assert.Contains(before, entry => entry.Name == "ReceiverShown");
        Assert.Equal(before.Count, layout.ReadEvents().Count);
        Assert.False(File.Exists(Path.Combine(layout.Output.Path, "report.json")));
        Assert.False(File.Exists(Path.Combine(layout.Output.Path, "exit.json")));
    }

    [Theory]
    [InlineData("Copy", 0, "Accepted")]
    [InlineData("None", 2, "Rejected")]
    [InlineData("Throw", 1, "Failed")]
    public void CommandExitReflectsReceiverPolicyAfterDisposal(string policy, int code, string status)
    {
        using var layout = new TestLayout(copyBinary: false);
        wpf.Invoke(() =>
        {
            var options = layout.Options("receive", policy);
            var output = ProbeOutputDirectory.Reserve(options, ProbePathEnvironment.Capture());
            var identity = ProbeProcessIdentity.Capture(options);
            ProbeFileDropReceiver? observed = null;
            var result = ProbeCommandRunner.Run(options, output, identity, receiver =>
            {
                observed = receiver;
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { @"C:\probe\unopened.txt" });
                receiver.Enter(data, DragDropEffects.Copy);
                receiver.Drop(data, DragDropEffects.Copy);
            });
            Assert.Equal(code, result);
            Assert.True(observed!.IsDisposed);
            var exit = layout.Read<ProbeProcessExitReport>("exit.json");
            Assert.Equal(code, exit.ExitCode);
            Assert.Equal(status, exit.Status);
            Assert.Equal(identity.RunId, observed.Report!.RunId);
        });
    }

    [Theory]
    [InlineData("prepare-before")]
    [InlineData("prepare-after")]
    [InlineData("receiver-before")]
    [InlineData("receiver-after")]
    [InlineData("recorder-creation")]
    [InlineData("dispose")]
    [InlineData("final-publication")]
    public void CommandFaultsPreservePartialEvidenceAndNeverPublishASecondTerminal(string fault)
    {
        using var layout = new TestLayout(copyBinary: false);
        wpf.Invoke(() =>
        {
            var options = layout.Options(fault.StartsWith("prepare", StringComparison.Ordinal) ? "prepare" : "receive", "Copy");
            var output = ProbeOutputDirectory.Reserve(options, ProbePathEnvironment.Capture());
            var identity = ProbeProcessIdentity.Capture(options);
            var disposeCount = 0;
            var result = ProbeCommandRunner.Run(options, output, identity,
                runReceiver: receiver =>
                {
                    if (fault == "receiver-before") throw new IOException("Before receiver terminal");
                    if (fault == "final-publication") Directory.CreateDirectory(Path.Combine(output.Path, "report.json"));
                    var data = new DataObject();
                    data.SetData(DataFormats.FileDrop, new[] { @"C:\probe\unopened.txt" });
                    receiver.Enter(data, DragDropEffects.Copy);
                    receiver.Drop(data, DragDropEffects.Copy);
                    if (fault == "receiver-after") throw new IOException("After receiver terminal");
                },
                prepare: (request, reserved, runId) =>
                {
                    if (fault == "prepare-after") ProbeFixtureBuilder.Build(request, reserved, runId);
                    throw new IOException("Controlled fixture failure");
                },
                recorderFactory: (reserved, terminal) =>
                {
                    if (fault == "recorder-creation") throw new IOException("Controlled recorder creation failure");
                    return new DisposalRecorder(new ProbeReceiverReportRecorder(reserved, terminal), () =>
                    {
                        disposeCount++;
                        if (fault == "dispose") throw new IOException("Controlled disposal failure");
                    });
                });
            Assert.Equal(1, result);
            var exit = layout.Read<ProbeProcessExitReport>("exit.json");
            Assert.Equal("Failed", exit.Status);
            Assert.Equal(1, exit.ExitCode);
            Assert.True(exit.Error is not null || exit.EvidenceError is not null || fault == "receiver-before");
            var files = Directory.EnumerateFiles(output.Path)
                .Where(path => Path.GetFileName(path) is "report.json" or "failure.json").ToArray();
            Assert.Equal(fault == "final-publication" ? 0 : 1, files.Length);
            if (fault is "dispose" or "receiver-after")
                Assert.Equal("Accepted", layout.Read<ProbeReceiverReport>("report.json").Status);
            if (fault == "dispose") Assert.Equal(1, disposeCount);
            if (fault == "prepare-after")
                ProbeFixtureManifestStore.LoadAndValidate(output, ProbeFixtureProfile.Mixed, TestLayout.Revision);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalDrivenWindowCloseIsQueuedOnceAndManualCloseCancelsTheQueuedWork(bool closeImmediately)
    {
        await wpf.InvokeAsync(async () =>
        {
            using var layout = new TestLayout(copyBinary: false);
            var options = layout.Options("receive", "Copy");
            var output = ProbeOutputDirectory.Reserve(options, ProbePathEnvironment.Capture());
            var receiver = new ProbeFileDropReceiver(ProbeReceiverEffect.Copy, TestLayout.Revision,
                new ProbeReceiverReportRecorder(output));
            var window = new ProbeFileDropReceiverWindow(receiver, closeOnTerminal: true);
            var closes = 0;
            window.Closed += (_, _) => closes++;
            try
            {
                window.Show();
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { @"C:\probe\unopened.txt" });
                receiver.Enter(data, DragDropEffects.Copy);
                DragEventArgs Drop()
                {
                    var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                        [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, window, new Point(10, 10)], null)!;
                    args.RoutedEvent = DragDrop.PreviewDropEvent;
                    window.RaiseEvent(args);
                    return args;
                }
                Assert.Equal(DragDropEffects.Copy, Drop().Effects);
                Assert.Equal(DragDropEffects.None, Drop().Effects);
                Assert.False(receiver.IsDisposed);
                if (closeImmediately) window.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(receiver.IsDisposed);
                Assert.Equal(1, closes);
                Assert.Equal("Accepted", layout.Read<ProbeReceiverReport>("report.json").Status);
                Assert.Single(layout.ReadEvents(), entry => entry.Name == "ReceiverTerminal");
            }
            finally
            {
                if (!receiver.IsDisposed) window.Close();
            }
            return true;
        });
    }

    private static void AssertIdentity(TestLayout layout, ProbeProcess process, ProbeProcessIdentity identity)
    {
        Assert.Equal(1, identity.ReportVersion);
        Assert.Equal("ProcessLifecycle", identity.EvidenceScope);
        Assert.Equal(process.Id, identity.ProcessId);
        Assert.Equal(TestLayout.Revision, identity.SourceRevision);
        Assert.Equal(layout.ExecutablePath, identity.ExecutablePath, ignoreCase: true);
        Assert.Equal(HashFile(layout.ExecutablePath), identity.ExecutableSha256);
        Assert.Equal(HashFile(Path.Combine(layout.BinaryRoot, "Vok.StageDragProbe.dll")), identity.AssemblySha256);
        Assert.True(Guid.TryParseExact(identity.RunId, "N", out _));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class DisposalRecorder(IProbeReceiverRecorder inner, Action dispose) : IProbeReceiverRecorder
    {
        public void Record(string name, object details) => inner.Record(name, details);
        public void Complete(ProbeReceiverReport report) => inner.Complete(report);
        public void Dispose() { inner.Dispose(); dispose(); }
    }

    private sealed record EventEntry(string Name, JsonElement Details);

    private sealed class TestLayout : IDisposable
    {
        internal const string Revision = "n2-1d-process-tests";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"vok-drag-process-tests-{Guid.NewGuid():N}");
        internal string AllowedRoot => Path.Combine(Root, "allowed");
        internal string RepositoryRoot => Path.Combine(Root, "repository");
        internal string BinaryRoot => Path.Combine(Root, "binary");
        internal string ExecutablePath => Path.Combine(BinaryRoot, "Vok.StageDragProbe.exe");
        internal string WatchdogPath => Path.Combine(Root, "watchdog.json");
        internal ProbeOutputDirectory Output => new(Path.Combine(AllowedRoot, "run"));
        internal bool KeepFailureEvidence { get; set; }

        internal TestLayout(bool copyBinary = true)
        {
            Directory.CreateDirectory(AllowedRoot);
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "src"));
            File.WriteAllText(Path.Combine(RepositoryRoot, "src", "Vok.sln"), "fixture repository");
            if (!copyBinary) return;
            Directory.CreateDirectory(BinaryRoot);
            foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var name = "Vok.StageDragProbe" + extension;
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(BinaryRoot, name), overwrite: false);
            }
        }

        internal StageDragProbeOptions Options(string mode, string policy) => StageDragProbeOptions.Parse(Arguments(mode, policy, []));
        internal ProbeProcess Start(string mode, string policy, string[]? extra = null) =>
            new(this, Arguments(mode, policy, extra ?? []));

        private string[] Arguments(string mode, string policy, string[] extra) =>
        [
            mode, "--allowed-root", AllowedRoot, "--output-root", Output.Path,
            "--repository-root", RepositoryRoot, "--source-revision", Revision,
            mode == "prepare" ? "--profile" : "--effect",
            mode == "prepare" && policy == "Copy" ? "mixed" : policy,
            .. extra,
        ];

        internal T Read<T>(string name) where T : notnull => ProbeReportWriter.Read<T>(Path.Combine(Output.Path, name));

        internal List<EventEntry> ReadEvents()
        {
            var path = Path.Combine(Output.Path, "events.jsonl");
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            var result = new List<EventEntry>();
            // A concurrently written last line is not evidence until its newline is present.
            foreach (var line in lines.Take(lines.Length - 1))
            {
                using var entry = JsonDocument.Parse(line);
                Assert.Equal(1, entry.RootElement.GetProperty("eventVersion").GetInt32());
                Assert.Equal(result.Count + 1, entry.RootElement.GetProperty("sequence").GetInt32());
                result.Add(new(entry.RootElement.GetProperty("name").GetString()!,
                    entry.RootElement.GetProperty("details").Clone()));
            }
            return result;
        }

        public void Dispose()
        {
            // A killed process is not a normal teardown: preserve the whole failure
            // bundle (including its exact binary), rather than deleting the evidence
            // or assuming process exit makes every image mapping deletable immediately.
            if (KeepFailureEvidence) return;
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var root = Path.GetFullPath(Root);
            if (string.Equals(root, temp, StringComparison.OrdinalIgnoreCase) || !ProbePathPolicy.IsSameOrDescendant(root, temp))
                throw new InvalidOperationException("Unsafe process test cleanup root.");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProbeProcess : IAsyncDisposable
    {
        // Conservative test watchdog only; actual state/events, never elapsed time, denote readiness/success.
        private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);
        private readonly TestLayout _layout;
        private readonly Process _process;
        internal int Id => _process.Id;
        internal bool HasExited => _process.HasExited;
        internal Task<string> StandardError { get; }
        private Task<string> StandardOutput { get; }

        internal ProbeProcess(TestLayout layout, string[] arguments)
        {
            _layout = layout;
            var start = new ProcessStartInfo(layout.ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = layout.RepositoryRoot,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Probe did not start.");
            StandardError = _process.StandardError.ReadToEndAsync();
            StandardOutput = _process.StandardOutput.ReadToEndAsync();
        }

        internal async Task WaitForEventAsync(string name)
        {
            var signals = Channel.CreateBounded<bool>(1);
            using var watcher = new FileSystemWatcher(_layout.AllowedRoot)
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, _) => signals.Writer.TryWrite(true);
            watcher.Changed += (_, _) => signals.Writer.TryWrite(true);
            watcher.Renamed += (_, _) => signals.Writer.TryWrite(true);
            watcher.Error += (_, args) => signals.Writer.TryComplete(args.GetException());
            _process.EnableRaisingEvents = true;
            _process.Exited += OnExited;
            void OnExited(object? sender, EventArgs args) => signals.Writer.TryWrite(true);
            watcher.EnableRaisingEvents = true;
            using var deadline = new CancellationTokenSource(WaitBudget);
            try
            {
                while (true)
                {
                    if (_layout.ReadEvents().Any(entry => entry.Name == name)) return;
                    if (_process.HasExited) throw new InvalidOperationException($"Probe exited before {name}: {await StandardError}");
                    await signals.Reader.ReadAsync(deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                await ExpireWatchdogAsync($"Missing event: {name}", WaitBudget);
                throw new TimeoutException($"Receiver did not publish {name}.");
            }
            finally { _process.Exited -= OnExited; }
        }

        internal async Task<int> WaitForExitAsync(TimeSpan? budget = null)
        {
            using var deadline = new CancellationTokenSource(budget ?? WaitBudget);
            try { await _process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                await ExpireWatchdogAsync("Process did not exit", budget ?? WaitBudget);
                throw new TimeoutException("Probe exceeded its external test watchdog.");
            }
            return _process.ExitCode;
        }

        private async Task ExpireWatchdogAsync(string reason, TimeSpan budget)
        {
            _layout.KeepFailureEvidence = true;
            try
            {
                ProbeReportWriter.PublishNew(new ProbeOutputDirectory(_layout.Root), "watchdog.json", new
                {
                    reportVersion = 1, evidenceScope = "ExternalWatchdog", status = "Failed", reason,
                    processId = Id, executablePath = _layout.ExecutablePath,
                    executableSha256 = HashFile(_layout.ExecutablePath), deadlineMilliseconds = budget.TotalMilliseconds,
                    lastEvent = _layout.ReadEvents().LastOrDefault()?.Name, observedUtc = DateTimeOffset.UtcNow,
                });
            }
            finally
            {
                // Retain the original Process handle: no lookup by name/PID and no unrelated descendants.
                if (!_process.HasExited)
                {
                    try { _process.Kill(); }
                    catch (InvalidOperationException) when (_process.HasExited) { }
                }
                await _process.WaitForExitAsync().WaitAsync(WaitBudget);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) await ExpireWatchdogAsync("Test cleanup before normal exit", WaitBudget);
                await Task.WhenAll(StandardError, StandardOutput).WaitAsync(WaitBudget);
            }
            finally { _process.Dispose(); }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(nint window);

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out int processId);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    }
}
