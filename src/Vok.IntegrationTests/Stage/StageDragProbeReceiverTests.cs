using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed class StageDragProbeReceiverTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void CopySnapshotsFormatsAndOrderedDuplicatePathsOnlyAtDrop()
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            string[] paths = [@"C:\probe\示例.txt", @"C:\probe\b.txt", @"C:\probe\示例.txt"];
            var data = new TestData { Payload = paths };
            Assert.Equal(DragDropEffects.Copy, receiver.Enter(data, DragDropEffects.All));
            for (var index = 0; index < 100; index++)
            {
                Assert.Equal(DragDropEffects.Copy, receiver.Over(DragDropEffects.Copy));
            }

            Assert.Equal(0, data.ReadCount);
            Assert.Equal(1, data.PresentCount);
            Assert.Equal(new[] { false, true }, data.FormatsCalls);
            Assert.Equal(DragDropEffects.Copy, receiver.Enter(data, DragDropEffects.Copy));
            Assert.Equal(2, data.FormatsCalls.Count);
            Assert.Equal(DragDropEffects.Copy, receiver.Drop(data, DragDropEffects.Copy));

            paths[0] = "changed-after-drop";
            data.NativeFormats[0] = "changed-after-enter";
            data.AllFormats[0] = "changed-after-enter";
            var report = Assert.IsType<ProbeReceiverReport>(receiver.Report);
            Assert.Equal("Accepted", report.Status);
            Assert.Equal("ReceiverObservation", report.EvidenceScope);
            Assert.Equal(1, report.Generation);
            Assert.Equal(2, report.EnterCount);
            Assert.Equal(100, report.OverCount);
            Assert.Equal(1, report.DropCount);
            Assert.Equal(1, report.FileDropReadCount);
            Assert.Equal(1, data.ReadCount);
            Assert.Equal(1, report.RawEffect);
            Assert.Equal(@"C:\probe\示例.txt", report.Paths[0]);
            Assert.Equal(report.Paths[0], report.Paths[2]);
            Assert.Equal(DataFormats.FileDrop, report.NativeFormats[0]);
            Assert.Equal(DataFormats.FileDrop, report.AllFormats[0]);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)report.Paths)[0] = "mutation");
            Assert.Single(recorder.Reports);
        });
    }

    [Theory]
    [InlineData("None", "Rejected", 0)]
    [InlineData("Throw", "Failed", 1)]
    public void ControlledPoliciesHaveOneTerminalAndNeverReadForeignPayload(
        string policyName, string status, int hoverEffect)
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            using var receiver = CreateReceiver(Enum.Parse<ProbeReceiverEffect>(policyName), recorder);
            var data = new TestData();
            Assert.Equal((DragDropEffects)hoverEffect, receiver.Enter(data, DragDropEffects.Copy));
            Assert.Equal((DragDropEffects)hoverEffect, receiver.Over(DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal(status, receiver.Report!.Status);
            Assert.Equal(0, receiver.Report.RawEffect);
            Assert.Equal(0, receiver.Report.FileDropReadCount);
            Assert.Equal(0, data.ReadCount);
            Assert.Empty(receiver.Report.Paths);
            if (policyName == "Throw") Assert.NotNull(receiver.Report.Error);
            Assert.Single(recorder.Reports);
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(3, 1)]
    [InlineData(-2147483647, 1)]
    public void CurrentAllowedEffectsDetermineCopyWithoutEverReturningMove(int allowed, int expected)
    {
        wpf.Invoke(() =>
        {
            using var receiver = CreateReceiver();
            var data = new TestData();
            receiver.Enter(data, DragDropEffects.Copy);
            Assert.Equal((DragDropEffects)expected, receiver.Over((DragDropEffects)allowed));
            Assert.Equal((DragDropEffects)expected, receiver.Drop(data, (DragDropEffects)allowed));
            Assert.Equal(expected, data.ReadCount);
            Assert.Equal(allowed, receiver.Report!.LastAllowedEffects);
        });
    }

    [Theory]
    [InlineData("native")]
    [InlineData("all")]
    [InlineData("present-enter")]
    [InlineData("present-drop")]
    [InlineData("read")]
    public void ForeignExceptionsAreRecordedAndCannotEscapeToDispatcher(string point)
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            var data = new TestData();
            data.BeforeCall = call =>
            {
                if (call == point || (point == "present-enter" && call == "present"))
                    throw new COMException("Controlled foreign failure");
            };
            receiver.Enter(data, DragDropEffects.Copy);
            if (point == "present-drop")
            {
                data.BeforeCall = call =>
                {
                    if (call == "present") throw new COMException("Withdrawn before Drop");
                };
            }

            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            var report = Assert.IsType<ProbeReceiverReport>(receiver.Report);
            Assert.Equal("Failed", report.Status);
            Assert.Equal(typeof(COMException).FullName, report.Error!.Type);
            Assert.Equal(0, report.RawEffect);
            Assert.Equal(point == "read" ? 1 : 0, data.ReadCount);
            Assert.Single(recorder.Reports);
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-type")]
    [InlineData("empty")]
    [InlineData("empty-entry")]
    public void InvalidPayloadIsRejectedAfterExactlyOneRead(string shape)
    {
        wpf.Invoke(() =>
        {
            using var receiver = CreateReceiver();
            var data = new TestData
            {
                Payload = shape switch
                {
                    "null" => null,
                    "wrong-type" => "not-a-path-array",
                    "empty" => Array.Empty<string>(),
                    _ => new[] { "" },
                },
            };
            receiver.Enter(data, DragDropEffects.Copy);
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal("InvalidFileDrop", receiver.Report!.Reason);
            Assert.Equal(1, data.ReadCount);
        });
    }

    [Fact]
    public void MissingOrWithdrawnFileDropAndDropWithoutEnterDoNotRead()
    {
        wpf.Invoke(() =>
        {
            using var receiver = CreateReceiver();
            var data = new TestData { Present = false };
            Assert.Equal(DragDropEffects.None, receiver.Enter(data, DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, receiver.Over(DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal("FileDropUnavailable", receiver.Report!.Reason);
            Assert.Equal(0, data.ReadCount);

            using var withdrawn = CreateReceiver();
            data.Present = true;
            withdrawn.Enter(data, DragDropEffects.Copy);
            data.Present = false;
            Assert.Equal(DragDropEffects.None, withdrawn.Drop(data, DragDropEffects.Copy));
            Assert.Equal(0, data.ReadCount);

            using var notEntered = CreateReceiver();
            Assert.Equal(DragDropEffects.None, notEntered.Over(DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, notEntered.Drop(data, DragDropEffects.Copy));
            Assert.Equal("DropWithoutEnter", notEntered.Report!.Reason);
            Assert.Equal(0, data.ReadCount);
        });
    }

    [Theory]
    [InlineData("leave", "Cancelled", "DragLeave")]
    [InlineData("cancel", "Cancelled", "Escape")]
    [InlineData("close", "Failed", "WindowClosedBeforeDrop")]
    [InlineData("watchdog", "Failed", "WatchdogExpired")]
    [InlineData("dispose", "Failed", "DisposedBeforeDrop")]
    public void CompetingTerminationsAreIdempotentAndLateEventsNeverRead(
        string end, string expectedStatus, string expectedReason)
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            var data = new TestData();
            receiver.Enter(data, DragDropEffects.Copy);
            switch (end)
            {
                case "leave": receiver.Leave(); break;
                case "cancel": receiver.Cancel(); break;
                case "close": receiver.CloseWindow(); break;
                case "watchdog": receiver.WatchdogExpired(); break;
                default: receiver.Dispose(); break;
            }

            var first = receiver.Report;
            data.BeforeCall = _ => throw new InvalidOperationException("Late data must not be touched");
            Assert.Equal(DragDropEffects.None, receiver.Enter(data, DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, receiver.Over(DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            receiver.Leave();
            receiver.Cancel();
            receiver.CloseWindow();
            receiver.WatchdogExpired();
            receiver.Dispose();
            receiver.Dispose();
            Assert.Same(first, receiver.Report);
            Assert.Equal(expectedStatus, receiver.Report!.Status);
            Assert.Equal(expectedReason, receiver.Report.Reason);
            Assert.Single(recorder.Reports);
            Assert.Equal(1, recorder.DisposeCount);
            Assert.Equal(0, data.ReadCount);
        });
    }

    [Fact]
    public void ReentrantDropIsClaimedOnceAndCloseDuringReadWins()
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            var data = new TestData();
            receiver.Enter(data, DragDropEffects.Copy);
            data.BeforeCall = point =>
            {
                if (point != "read") return;
                Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
                Assert.Equal(DragDropEffects.None, receiver.Enter(data, DragDropEffects.Copy));
                receiver.CloseWindow();
            };
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal("WindowClosedBeforeDrop", receiver.Report!.Reason);
            Assert.Empty(receiver.Report.Paths);
            Assert.Equal(1, data.ReadCount);
            Assert.Single(recorder.Reports);
        });
    }

    [Theory]
    [InlineData("native")]
    [InlineData("all")]
    [InlineData("present")]
    public void CloseDuringMetadataProbeStopsSubsequentForeignCalls(string stopAt)
    {
        wpf.Invoke(() =>
        {
            using var receiver = CreateReceiver();
            var data = new TestData();
            var closed = false;
            data.BeforeCall = point =>
            {
                Assert.False(closed);
                if (point != stopAt) return;
                receiver.CloseWindow();
                closed = true;
            };
            Assert.Equal(DragDropEffects.None, receiver.Enter(data, DragDropEffects.Copy));
            Assert.True(closed);
            Assert.Equal("WindowClosedBeforeDrop", receiver.Report!.Reason);
            Assert.Equal(0, data.ReadCount);
        });
    }

    [Theory]
    [InlineData("ProcessStarted")]
    [InlineData("OutputReserved")]
    [InlineData("ReceiverReady")]
    [InlineData("FormatsObserved")]
    [InlineData("DragOver")]
    [InlineData("Drop")]
    [InlineData("IgnoredDrop")]
    [InlineData("ReceiverTerminal")]
    [InlineData("complete")]
    public void EvidenceFailuresReturnNoneAndHaveNoSecondPublicationAttempt(string fault)
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder { Fault = fault };
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            receiver.MarkReady(123);
            var data = new TestData();
            receiver.Enter(data, DragDropEffects.Copy);
            receiver.Over(DragDropEffects.Copy);
            if (fault == "IgnoredDrop")
            {
                data.BeforeCall = point =>
                {
                    if (point == "read") receiver.Drop(data, DragDropEffects.Copy);
                };
            }

            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal("Failed", receiver.Report!.Status);
            Assert.Equal(0, receiver.Report.RawEffect);
            Assert.NotNull(receiver.EvidenceError);
            receiver.CloseWindow();
            receiver.WatchdogExpired();
            receiver.Drop(data, DragDropEffects.Copy);
            Assert.Equal(1, recorder.CompleteCount);
        });
    }

    [Fact]
    public void OldReceiverCannotAffectNewInstanceAndWrongThreadIsRejected()
    {
        ProbeFileDropReceiver? receiverFromOtherThread = null;
        wpf.Invoke(() =>
        {
            var oldRecorder = new TestRecorder();
            var newRecorder = new TestRecorder();
            using var oldReceiver = CreateReceiver(ProbeReceiverEffect.Copy, oldRecorder);
            using var newReceiver = CreateReceiver(ProbeReceiverEffect.Copy, newRecorder);
            var data = new TestData();
            oldReceiver.Enter(data, DragDropEffects.Copy);
            oldReceiver.Cancel();
            newReceiver.Enter(data, DragDropEffects.Copy);
            oldReceiver.WatchdogExpired();
            oldReceiver.Drop(data, DragDropEffects.Copy);
            Assert.Null(newReceiver.Report);
            Assert.Equal(DragDropEffects.Copy, newReceiver.Drop(data, DragDropEffects.Copy));
            Assert.NotEqual(oldReceiver.RunId, newReceiver.RunId);
            Assert.Single(oldRecorder.Reports);
            Assert.Single(newRecorder.Reports);
            receiverFromOtherThread = newReceiver;
        });
        Assert.Throws<InvalidOperationException>(() => receiverFromOtherThread!.CloseWindow());
    }

    [Theory]
    [InlineData("Copy", "report.json", "Accepted")]
    [InlineData("None", "report.json", "Rejected")]
    [InlineData("Throw", "failure.json", "Failed")]
    public void RealReportsAreCreateOnlyAndNeverChangeFixtureSources(
        string policy, string reportName, string expectedStatus)
    {
        using var layout = new TestOutputs();
        wpf.Invoke(() =>
        {
            var sourceOptions = layout.Options("sources", ProbeFixtureProfile.Duplicate);
            var sourceOutput = ProbeOutputDirectory.Reserve(sourceOptions, layout.Environment);
            var before = ProbeFixtureBuilder.Build(sourceOptions, sourceOutput);
            var output = layout.Reserve("receiver");
            var recorder = new ProbeReceiverReportRecorder(output);
            using (var receiver = CreateReceiver(Enum.Parse<ProbeReceiverEffect>(policy), recorder))
            {
                receiver.MarkReady(123);
                Assert.True(new FileInfo(Path.Combine(output.Path, "events.jsonl")).Length > 0);
                var data = new TestData { Payload = before.OrderedPaths.ToArray() };
                receiver.Enter(data, DragDropEffects.Copy);
                receiver.Over(DragDropEffects.Copy);
                receiver.Drop(data, DragDropEffects.Copy);
                var firstBytes = File.ReadAllBytes(Path.Combine(output.Path, reportName));
                receiver.Drop(data, DragDropEffects.Copy);
                receiver.CloseWindow();
                Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(output.Path, reportName)));
                Assert.Throws<InvalidOperationException>(() => recorder.Complete(receiver.Report!));
            }

            var report = ProbeReportWriter.Read<ProbeReceiverReport>(Path.Combine(output.Path, reportName));
            Assert.Equal(1, report.ReportVersion);
            Assert.Equal(expectedStatus, report.Status);
            Assert.Equal(123, report.WindowHandle);
            Assert.Single(Directory.EnumerateFiles(output.Path, "*.json"));
            Assert.Empty(Directory.EnumerateFiles(output.Path, "*.pending"));
            Assert.Throws<IOException>(() => new ProbeReceiverReportRecorder(output));
            var lines = File.ReadAllLines(Path.Combine(output.Path, "events.jsonl"));
            var names = new List<string>();
            for (var index = 0; index < lines.Length; index++)
            {
                using var entry = JsonDocument.Parse(lines[index]);
                Assert.Equal(index + 1, entry.RootElement.GetProperty("sequence").GetInt32());
                names.Add(entry.RootElement.GetProperty("name").GetString()!);
            }

            Assert.Equal("ProcessStarted", names[0]);
            Assert.Equal("OutputReserved", names[1]);
            Assert.Contains("ReceiverReady", names);
            Assert.Single(names, name => name == "ReceiverTerminal");
            Assert.Contains("IgnoredDrop", names);
            var after = ProbeFixtureManifestStore.LoadAndValidate(
                sourceOutput, ProbeFixtureProfile.Duplicate, "receiver-tests");
            Assert.Equal(before.Manifest.SourceTreeDigest, after.Manifest.SourceTreeDigest);
            Assert.Equal(before.Manifest.OrderedOccurrenceDigest, after.Manifest.OrderedOccurrenceDigest);
        });
    }

    [Fact]
    public void FinalReportCollisionPreservesExistingEvidenceAndReturnsNone()
    {
        using var layout = new TestOutputs();
        wpf.Invoke(() =>
        {
            var output = layout.Reserve("collision");
            using var receiver = CreateReceiver(ProbeReceiverEffect.Copy, new ProbeReceiverReportRecorder(output));
            var existingPath = Path.Combine(output.Path, "failure.json");
            File.WriteAllText(existingPath, "existing evidence");
            var data = new TestData();
            receiver.Enter(data, DragDropEffects.Copy);
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal("existing evidence", File.ReadAllText(existingPath));
            Assert.Equal("ReportWriteFailed", receiver.Report!.Reason);
            Assert.False(File.Exists(Path.Combine(output.Path, "report.json")));
        });
    }

    [Theory]
    [InlineData("Copy", "Accepted", 1)]
    [InlineData("None", "Rejected", 0)]
    [InlineData("Throw", "Failed", 0)]
    public void WpfWindowRoutesEventsHandlesEffectsAndReleasesOnClose(
        string policy, string expectedStatus, int expectedEffect)
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            var receiver = CreateReceiver(Enum.Parse<ProbeReceiverEffect>(policy), recorder);
            var window = new ProbeFileDropReceiverWindow(receiver);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True(window.AllowDrop);
                Assert.False(window.AllowsTransparency);
                Assert.False(window.Topmost);
                var content = (UIElement)window.Content;
                Assert.False(content.IsHitTestVisible);
                var center = content.TranslatePoint(
                    new Point(content.RenderSize.Width / 2, content.RenderSize.Height / 2), window);
                var hitTarget = Assert.IsAssignableFrom<UIElement>(window.InputHitTest(center));
                Assert.True(hitTarget.AllowDrop);
                // The template background can be the hit target. What matters is a
                // stable surface across passive text, with one route owned by Window.
                foreach (var x in new[] { 0.01, 0.25, 0.5, 0.75, 0.99 })
                foreach (var y in new[] { 0.01, 0.25, 0.5, 0.75, 0.99 })
                {
                    var point = content.TranslatePoint(
                        new Point(content.RenderSize.Width * x, content.RenderSize.Height * y), window);
                    Assert.Same(hitTarget, window.InputHitTest(point));
                }

                var data = new TestData();
                Raise(hitTarget, data, DragDrop.PreviewDragEnterEvent);
                for (var index = 0; index < 5; index++) Raise(hitTarget, data, DragDrop.PreviewDragOverEvent);
                Assert.Equal(0, data.ReadCount);
                var drop = Raise(hitTarget, data, DragDrop.PreviewDropEvent);
                Assert.True(drop.Handled);
                Assert.Equal((DragDropEffects)expectedEffect, drop.Effects);
                Assert.Equal(expectedStatus, receiver.Report!.Status);
                Assert.NotEqual(0, receiver.Report.WindowHandle);
                Assert.Single(recorder.Events, name => name == "ReceiverReady");
                Raise(hitTarget, data, DragDrop.PreviewDragLeaveEvent);
            }
            finally
            {
                window.Close();
            }

            Assert.True(receiver.IsDisposed);
            Assert.Equal(1, recorder.DisposeCount);
            Assert.Single(recorder.Reports);
        });
    }

    [Theory]
    [InlineData("close", "WindowClosedBeforeDrop")]
    [InlineData("escape", "Escape")]
    [InlineData("leave", "DragLeave")]
    [InlineData("watchdog", "WatchdogExpired")]
    public void WpfCloseEscapeAndLeaveInsideWindowKeepFailureEvidence(string stop, string reason)
    {
        using var layout = new TestOutputs();
        wpf.Invoke(() =>
        {
            var output = layout.Reserve("window");
            var receiver = CreateReceiver(ProbeReceiverEffect.Copy, new ProbeReceiverReportRecorder(output));
            var window = new ProbeFileDropReceiverWindow(receiver);
            try
            {
                window.Show();
                if (stop == "escape")
                {
                    window.RaiseEvent(new KeyEventArgs(
                        Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                else if (stop == "leave")
                {
                    var data = new TestData();
                    Raise(window, data, DragDrop.PreviewDragEnterEvent);
                    Raise(window, data, DragDrop.PreviewDragLeaveEvent);
                    Assert.Equal(0, data.ReadCount);
                }
                else if (stop == "watchdog")
                {
                    // Only the host's failure endpoint; no timer or OLE is started here.
                    receiver.WatchdogExpired();
                }
            }
            finally
            {
                if (!receiver.IsDisposed) window.Close();
            }

            var report = ProbeReportWriter.Read<ProbeReceiverReport>(Path.Combine(output.Path, "failure.json"));
            Assert.Equal(reason, report.Reason);
            Assert.Equal(0, report.RawEffect);
            Assert.True(receiver.IsDisposed);
            Assert.Single(Directory.EnumerateFiles(output.Path, "*.json"));
        });
    }

    [Fact]
    public void WpfCloseDuringForeignReadDisposesOnceAndDiscardsTheReturningPayload()
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder();
            var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            var window = new ProbeFileDropReceiverWindow(receiver);
            try
            {
                window.Show();
                var data = new TestData();
                Raise(window, data, DragDrop.PreviewDragEnterEvent);
                data.BeforeCall = point =>
                {
                    if (point == "read") window.Close();
                };
                var drop = Raise(window, data, DragDrop.PreviewDropEvent);
                Assert.Equal(DragDropEffects.None, drop.Effects);
                Assert.Equal("WindowClosedBeforeDrop", receiver.Report!.Reason);
                Assert.Empty(receiver.Report.Paths);
                Assert.Equal(1, data.ReadCount);
                Assert.True(receiver.IsDisposed);
                Assert.Equal(1, recorder.DisposeCount);
                Assert.Single(recorder.Reports);
            }
            finally
            {
                if (!receiver.IsDisposed) window.Close();
            }
        });
    }

    [Fact]
    public void WpfCancelledCloseKeepsReceiverUsableAndDisposeFailureDoesNotEscape()
    {
        wpf.Invoke(() =>
        {
            var recorder = new TestRecorder { Fault = "dispose" };
            var receiver = CreateReceiver(ProbeReceiverEffect.Copy, recorder);
            var window = new ProbeFileDropReceiverWindow(receiver);
            var cancelClose = true;
            window.Closing += (_, args) => args.Cancel = cancelClose;
            try
            {
                window.Show();
                window.Close();
                Assert.True(window.IsVisible);
                Assert.False(receiver.IsDisposed);
                Assert.Null(receiver.Report);
                var data = new TestData();
                Raise(window, data, DragDrop.PreviewDragEnterEvent);
                Assert.Equal(DragDropEffects.Copy, Raise(window, data, DragDrop.PreviewDropEvent).Effects);
            }
            finally
            {
                cancelClose = false;
                window.Close();
            }

            receiver.Dispose();
            Assert.True(receiver.IsDisposed);
            Assert.NotNull(receiver.EvidenceError);
            Assert.Equal("Accepted", receiver.Report!.Status);
            Assert.Equal(1, recorder.DisposeCount);
            Assert.Single(recorder.Reports);
        });
    }

    private static ProbeFileDropReceiver CreateReceiver(
        ProbeReceiverEffect policy = ProbeReceiverEffect.Copy,
        IProbeReceiverRecorder? recorder = null) => new(policy, "receiver-tests", recorder ?? new TestRecorder());

    private static DragEventArgs Raise(UIElement target, IDataObject data, RoutedEvent routedEvent)
    {
        var args = Assert.IsType<DragEventArgs>(Activator.CreateInstance(
            typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, target, new Point(10, 10)], null));
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        Assert.True(args.Handled);
        return args;
    }

    private sealed class TestRecorder : IProbeReceiverRecorder
    {
        public List<ProbeReceiverReport> Reports { get; } = [];
        public List<string> Events { get; } = [];
        public string? Fault { get; init; }
        public int CompleteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void Record(string name, object details)
        {
            if (name == Fault) throw new IOException("Controlled event writer failure");
            Events.Add(name);
        }

        public void Complete(ProbeReceiverReport report)
        {
            CompleteCount++;
            if (Fault == "complete") throw new IOException("Controlled final writer failure");
            Reports.Add(report);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (Fault == "dispose") throw new IOException("Controlled event log disposal failure");
        }
    }

    private sealed class TestData : IDataObject
    {
        public object? Payload { get; set; } = new[] { @"C:\probe\sample.txt" };
        public bool Present { get; set; } = true;
        public Action<string>? BeforeCall { get; set; }
        public int ReadCount { get; private set; }
        public int PresentCount { get; private set; }
        public List<bool> FormatsCalls { get; } = [];
        public string[] NativeFormats { get; } = [DataFormats.FileDrop, "Preferred DropEffect"];
        public string[] AllFormats { get; } = [DataFormats.FileDrop, "Preferred DropEffect", "test/synthesized"];

        public object GetData(string format, bool autoConvert)
        {
            Assert.Equal(DataFormats.FileDrop, format);
            Assert.True(autoConvert);
            ReadCount++;
            BeforeCall?.Invoke("read");
            return Payload!;
        }

        public bool GetDataPresent(string format, bool autoConvert)
        {
            if (format == ProbeOutboundDataObject.PreferredDropEffectFormat)
            {
                Assert.False(autoConvert);
                return false;
            }
            Assert.Equal(DataFormats.FileDrop, format);
            Assert.True(autoConvert);
            PresentCount++;
            BeforeCall?.Invoke("present");
            return Present;
        }

        public string[] GetFormats(bool autoConvert)
        {
            FormatsCalls.Add(autoConvert);
            BeforeCall?.Invoke(autoConvert ? "all" : "native");
            return autoConvert ? AllFormats : NativeFormats;
        }

        public object GetData(string format) => GetData(format, true);
        public object GetData(Type format) => GetData(format.FullName!, true);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, true);
        public string[] GetFormats() => GetFormats(true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class TestOutputs : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"vok-receiver-tests-{Guid.NewGuid():N}");
        private readonly string _repository;
        private readonly string _allowed;

        public TestOutputs()
        {
            _repository = Path.Combine(_root, "repository");
            _allowed = Path.Combine(_root, "allowed");
            Directory.CreateDirectory(Path.Combine(_repository, "src"));
            File.WriteAllText(Path.Combine(_repository, "src", "Vok.sln"), "fixture repository");
            Directory.CreateDirectory(_allowed);
            Environment = new ProbePathEnvironment(Path.Combine(_root, "product"), Path.Combine(_root, "profile"));
        }

        public ProbePathEnvironment Environment { get; }

        public StageDragProbeOptions Options(string name, ProbeFixtureProfile? profile = null) => new(
            profile is null ? StageDragProbeVerb.Receive : StageDragProbeVerb.Prepare,
            _allowed, Path.Combine(_allowed, name), _repository, "receiver-tests", profile,
            profile is null ? ProbeReceiverEffect.Copy : null);

        public ProbeOutputDirectory Reserve(string name) => ProbeOutputDirectory.Reserve(Options(name), Environment);

        public void Dispose()
        {
            var root = Path.GetFullPath(_root);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (string.Equals(root, temp, StringComparison.OrdinalIgnoreCase) ||
                !ProbePathPolicy.IsSameOrDescendant(root, temp))
                throw new InvalidOperationException("Unsafe test cleanup root.");
            Directory.Delete(root, recursive: true);
        }
    }
}
