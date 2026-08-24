using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Diagnostics;
using Xunit;

namespace Vok.IntegrationTests.Diagnostics;

public sealed partial class Win32WorkspaceDiagnosticPublisherTests
{
    [Fact]
    public void SeqlockReadKeepsGenerationTargetAndRestoreEvidenceCoherent()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var publisher = new Win32WorkspaceDiagnosticPublisher();
            try
            {
                publisher.Publish(handle, CreateSnapshot(41));
                var first = ReadCoherent(handle);
                publisher.Publish(handle, CreateSnapshot(42));
                var second = ReadCoherent(handle);

                Assert.Equal(41, first.Generation);
                Assert.Equal(41 * 0x10, first.CurrentTargetWindow);
                Assert.Equal(41, first.RestoreGeneration);
                Assert.Equal((41 * 0x10) + 1, first.RestoreTargetFocusWindow);
                Assert.Equal(41_090, first.RestoreReturnedTimestamp);
                Assert.True(first.Sequence > 0 && (first.Sequence & 1) == 0);

                Assert.Equal(42, second.Generation);
                Assert.Equal(42 * 0x10, second.CurrentTargetWindow);
                Assert.Equal(42, second.RestoreGeneration);
                Assert.Equal((42 * 0x10) + 1, second.RestoreTargetFocusWindow);
                Assert.Equal(42_090, second.RestoreReturnedTimestamp);
                Assert.True(second.Sequence > first.Sequence && (second.Sequence & 1) == 0);
            }
            finally
            {
                publisher.Clear(handle);
                window.Close();
            }

            Assert.Equal(0, GetRaw(handle, "Sequence"));
        });
    }

    private static WorkspaceDiagnosticSnapshot CreateSnapshot(long generation)
    {
        var target = new ForegroundTarget(
            (nint)(generation * 0x10),
            (uint)(generation + 100),
            (uint)(generation + 200),
            (nint)((generation * 0x10) + 1),
            (uint)(generation + 100),
            (uint)(generation + 200),
            ForegroundWindowRelationship.Descendant);
        var result = new ForegroundRestoreResult(
            ForegroundRestoreStatus.ForegroundConfirmedFocusChanged,
            target.WindowHandle,
            target.WindowHandle,
            SetForegroundAttempted: true,
            SetForegroundSucceeded: true,
            TargetWindowValid: true,
            TargetIdentityMatches: true,
            TargetProcessId: target.ProcessId,
            TargetThreadId: target.ThreadId,
            CapturedFocusWindowValid: true,
            CapturedFocusIdentityMatches: true,
            CapturedFocusProcessId: target.FocusedProcessId,
            CapturedFocusThreadId: target.FocusedThreadId,
            CapturedFocusRelationship: ForegroundWindowRelationship.Descendant,
            ObservedFocusProcessId: target.ProcessId,
            ObservedFocusThreadId: target.ThreadId,
            ObservedFocusRelationship: ForegroundWindowRelationship.SameWindow);
        var restore = new WorkspaceRestoreDiagnosticSnapshot(
            generation,
            WorkspaceHideReason.Escape,
            target,
            generation * 1000,
            (generation * 1000) + 10,
            (generation * 1000) + 20,
            (generation * 1000) + 30,
            true,
            (generation * 1000) + 80,
            (generation * 1000) + 90,
            false,
            result);
        return new WorkspaceDiagnosticSnapshot(
            generation,
            WorkspaceState.Hiding,
            WorkspaceDiagnosticFlags.Topmost,
            generation * 100,
            0,
            0,
            generation - 1,
            generation * 1000,
            0,
            ForegroundRestoreStatus.ForegroundConfirmedFocusChanged,
            target,
            restore);
    }

    private static PublishedSnapshot ReadCoherent(nint handle)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var sequenceBefore = GetRaw(handle, "Sequence");
            if (sequenceBefore == 0 || (sequenceBefore & 1) != 0)
            {
                continue;
            }

            var snapshot = new PublishedSnapshot(
                sequenceBefore,
                GetEncoded(handle, "Generation"),
                GetEncoded(handle, "CurrentTargetWindow"),
                GetEncoded(handle, "RestoreGeneration"),
                GetEncoded(handle, "RestoreTargetFocusWindow"),
                GetEncoded(handle, "RestoreReturnedTimestamp"));
            var sequenceAfter = GetRaw(handle, "Sequence");
            if (sequenceBefore == sequenceAfter && (sequenceAfter & 1) == 0)
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("Could not obtain a coherent diagnostic snapshot.");
    }

    private static long GetRaw(nint handle, string suffix) =>
        GetProp(handle, Win32WorkspaceDiagnosticPublisher.Prefix + suffix);

    private static long GetEncoded(nint handle, string suffix)
    {
        var raw = GetRaw(handle, suffix);
        return raw == 0 ? -1 : raw - 1;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed record PublishedSnapshot(
        long Sequence,
        long Generation,
        long CurrentTargetWindow,
        long RestoreGeneration,
        long RestoreTargetFocusWindow,
        long RestoreReturnedTimestamp);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetProp(nint window, string name);
}
