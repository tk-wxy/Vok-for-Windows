using System.IO;
using System.Threading.Channels;
using Vok.Core.Clipboard;
using Vok.Features.Clipboard;
using Vok.Hosting;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardControllerOwnershipTests
{
    [Fact]
    public async Task NormalizerRejectionStillDisposesDeliveredImageOwnership()
    {
        var lease = new TrackingLease([1, 2, 3]);
        var reservation = new TrackingReservation(19);
        var format = new ClipboardFormatSnapshot(
            0,
            "PNG",
            ClipboardRepresentationKind.Png,
            ClipboardFormatRole.Content,
            ClipboardFormatCaptureState.Captured,
            ReadOnlyMemory<byte>.Empty,
            SourceByteLength: 3,
            ImageHeader: new ClipboardImageHeader(2, 2, 16),
            PayloadLease: lease,
            Reservation: reservation);
        var capture = new ClipboardCaptureResult(
            1,
            DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Captured,
            [format]);
        using var platform = new FakePlatformService();
        var repository = new UnusedRepository();
        using var viewModel = new ClipboardViewModel(repository);
        using var controller = new ClipboardController(
            platform,
            new RejectingNormalizer(),
            repository,
            viewModel,
            new ImmediateWorkspaceWindow());
        await controller.StartAsync();

        platform.Publish(capture);
        await lease.Disposed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(capture.IsDisposed);
        Assert.True(format.IsDisposed);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, reservation.DisposeCount);
    }

    private sealed class FakePlatformService : IClipboardPlatformService
    {
        private readonly Channel<ClipboardCaptureResult> _captures =
            Channel.CreateUnbounded<ClipboardCaptureResult>();

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IAsyncEnumerable<ClipboardCaptureResult> WatchAsync(
            CancellationToken cancellationToken = default) =>
            _captures.Reader.ReadAllAsync(cancellationToken);

        public Task<ClipboardWriteReceipt> WriteAsync(
            ClipboardReplayPayload payload,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsCurrentAsync(
            ClipboardWriteReceipt receipt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ClipboardDiagnosticSnapshot GetDiagnosticSnapshot() =>
            throw new NotSupportedException();

        public void Publish(ClipboardCaptureResult capture) =>
            Assert.True(_captures.Writer.TryWrite(capture));

        public void Dispose() => _captures.Writer.TryComplete();
    }

    private sealed class RejectingNormalizer : IClipboardCaptureNormalizer
    {
        public ClipboardNormalizedContent? Normalize(ClipboardCaptureResult capture) =>
            null;
    }

    private sealed class TrackingLease(byte[] bytes) : IClipboardPayloadLease
    {
        public long Length => bytes.Length;

        public int DisposeCount { get; private set; }

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream OpenRead() => new MemoryStream(bytes, writable: false);

        public void Dispose()
        {
            DisposeCount++;
            Disposed.TrySetResult();
        }
    }

    private sealed class TrackingReservation(long reservedBytes)
        : IClipboardPayloadReservation
    {
        public long ReservedBytes { get; } = reservedBytes;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class UnusedRepository : IClipboardHistoryRepository
    {
        public Task<ClipboardPage> LoadPageAsync(
            ClipboardPageCursor? after,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardEntry> UpsertTextAsync(
            ClipboardTextContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardEntry> UpsertFileListAsync(
            ClipboardFileListContent content,
            DateTimeOffset capturedUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardReplayPayload?> GetReplayPayloadAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardEntry?> SetPinnedAsync(
            Guid entryId,
            bool isPinned,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ImmediateWorkspaceWindow : IWorkspaceWindow
    {
        public event EventHandler? Activated
        {
            add { }
            remove { }
        }

        public event EventHandler? ApplicationDeactivated
        {
            add { }
            remove { }
        }

        public event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? InputAnchorFocused
        {
            add { }
            remove { }
        }

        public event EventHandler<WorkspaceExternalDragStartedEventArgs>? ExternalDragStarted
        {
            add { }
            remove { }
        }

        public event EventHandler<WorkspaceExternalDragEndedEventArgs>? ExternalDragEnded
        {
            add { }
            remove { }
        }

        public bool IsVisible => false;

        public bool HasInputAnchorFocus => false;

        public nint EnsureHandle() => 0;

        public void Show() => throw new NotSupportedException();

        public void Hide() => throw new NotSupportedException();

        public bool Activate() => false;

        public bool FocusInputAnchor() => false;

        public void PostInput(Action action) => action();

        public void PostNormal(Action action) => action();

        public void PostBackground(Action action) => action();

        public IDisposable ScheduleNormal(TimeSpan dueTime, Action action) =>
            throw new NotSupportedException();
    }
}
