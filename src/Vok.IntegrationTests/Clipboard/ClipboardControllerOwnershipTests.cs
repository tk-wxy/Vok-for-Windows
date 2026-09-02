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
    public async Task ThousandCaptureListenerRunStaysResponsiveAndAppliesRetention()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Vok.Tests",
            $"clipboard-listener-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var platform = new FakePlatformService();
            using var repository = new Vok.Infrastructure.Windows.Persistence
                .SqliteClipboardHistoryRepository(Path.Combine(directory, "vok.db"));
            using var viewModel = new ClipboardViewModel(repository);
            await viewModel.InitializeAsync();
            var normalizer = new SequenceTextNormalizer();
            using var controller = new ClipboardController(
                platform,
                normalizer,
                repository,
                viewModel,
                new ImmediateWorkspaceWindow(),
                imageCapture: null,
                maintenance: repository,
                retentionOptions: new ClipboardRetentionOptions(
                    maximumUnpinnedEntryCount: 100,
                    maximumAge: TimeSpan.FromDays(30),
                    maximumTotalBytes: 16 * 1024 * 1024));
            await controller.StartAsync();

            for (uint sequence = 1; sequence <= 1000; sequence++)
            {
                platform.Publish(new ClipboardCaptureResult(
                    sequence,
                    DateTimeOffset.UtcNow,
                    ClipboardCaptureDisposition.Captured,
                    []));
            }

            await WaitUntilAsync(
                () => normalizer.CallCount == 1000 &&
                    viewModel.Items.FirstOrDefault()?.Entry.PlainPreview == "capture-1000",
                TestContext.Current.CancellationToken,
                timeout: TimeSpan.FromSeconds(30));

            var page = await repository.LoadPageAsync(
                null,
                64,
                TestContext.Current.CancellationToken);
            Assert.Equal(1000, normalizer.CallCount);
            Assert.Equal(100, page.TotalCount);
            Assert.Equal(100, viewModel.TotalCount);
            Assert.Equal("capture-1000", page.Items[0].PlainPreview);
            Assert.Equal("capture-901", (await repository.LoadPageAsync(
                page.NextCursor,
                64,
                TestContext.Current.CancellationToken)).Items[^1].PlainPreview);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DisabledImageServiceStillDisposesDeliveredImageOwnership()
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

    [Fact]
    public async Task ImageCaptureUsesImagePipelineAndPublishesCommittedEntry()
    {
        var first = CreateImageCapture(11);
        var normalizer = new CountingNormalizer();
        var imageCapture = new SequencedImageCaptureService(
            static _ => Task.FromResult(ImageEntry(1)));
        using var platform = new FakePlatformService();
        var repository = new UnusedRepository();
        using var viewModel = new ClipboardViewModel(repository);
        using var controller = new ClipboardController(
            platform,
            normalizer,
            repository,
            viewModel,
            new ImmediateWorkspaceWindow(),
            imageCapture);
        await controller.StartAsync();

        platform.Publish(first.Capture);
        await WaitUntilAsync(
            () => viewModel.Items.Count == 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, imageCapture.CallCount);
        Assert.Equal(0, normalizer.CallCount);
        Assert.True(first.Capture.IsDisposed);
        Assert.Equal(1, first.Lease.DisposeCount);
        Assert.Equal(1, first.Reservation.DisposeCount);
        Assert.True(Assert.Single(viewModel.Items).IsImage);
    }

    [Fact]
    public async Task ImageFailureDoesNotTerminateFollowingImageCapture()
    {
        var first = CreateImageCapture(21);
        var second = CreateImageCapture(22);
        var imageCapture = new SequencedImageCaptureService(call =>
            call == 1
                ? Task.FromException<ClipboardEntry>(
                    new InvalidDataException("deliberate image failure"))
                : Task.FromResult(ImageEntry(2)));
        using var platform = new FakePlatformService();
        var repository = new UnusedRepository();
        using var viewModel = new ClipboardViewModel(repository);
        using var controller = new ClipboardController(
            platform,
            new CountingNormalizer(),
            repository,
            viewModel,
            new ImmediateWorkspaceWindow(),
            imageCapture);
        await controller.StartAsync();

        platform.Publish(first.Capture);
        platform.Publish(second.Capture);
        await WaitUntilAsync(
            () => viewModel.Items.Count == 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, imageCapture.CallCount);
        Assert.True(first.Capture.IsDisposed);
        Assert.True(second.Capture.IsDisposed);
        Assert.Equal(1, first.Lease.DisposeCount);
        Assert.Equal(1, second.Lease.DisposeCount);
    }

    [Fact]
    public async Task CaptureCompletingAcrossClearIsRemovedInsteadOfReappearing()
    {
        var tracked = CreateImageCapture(25);
        var committed = ImageEntry(25);
        var imageCapture = new ControlledImageCaptureService(committed);
        var maintenance = new TrackingMaintenanceService();
        using var platform = new FakePlatformService();
        var repository = new UnusedRepository();
        using var viewModel = new ClipboardViewModel(
            repository,
            filePresentation: null,
            thumbnailCache: null,
            imageAssets: null,
            imageHistoryEnabled: false,
            imageFeatureUnavailableStatus: null,
            maintenanceService: maintenance);
        viewModel.ApplyCaptured(ImageEntry(24));
        using var controller = new ClipboardController(
            platform,
            new CountingNormalizer(),
            repository,
            viewModel,
            new ImmediateWorkspaceWindow(),
            imageCapture,
            maintenance);
        await controller.StartAsync();

        platform.Publish(tracked.Capture);
        await imageCapture.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await viewModel.ClearHistoryCommand.ExecuteAsync(null);
        await viewModel.ClearHistoryCommand.ExecuteAsync(null);
        imageCapture.Release();

        await WaitUntilAsync(
            () => maintenance.RemovedEntryIds.Contains(committed.Id),
            TestContext.Current.CancellationToken);

        Assert.Empty(viewModel.Items);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.True(tracked.Capture.IsDisposed);
        Assert.Equal(1, tracked.Lease.DisposeCount);
    }

    [Fact]
    public async Task DisposeCancelsActiveImageCaptureAndReleasesOwnership()
    {
        var tracked = CreateImageCapture(31);
        var imageCapture = new BlockingImageCaptureService();
        using var platform = new FakePlatformService();
        var repository = new UnusedRepository();
        using var viewModel = new ClipboardViewModel(repository);
        using var controller = new ClipboardController(
            platform,
            new CountingNormalizer(),
            repository,
            viewModel,
            new ImmediateWorkspaceWindow(),
            imageCapture);
        await controller.StartAsync();

        platform.Publish(tracked.Capture);
        await imageCapture.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        controller.Dispose();

        await tracked.Lease.Disposed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(tracked.Capture.IsDisposed);
        Assert.Equal(1, tracked.Lease.DisposeCount);
        Assert.Equal(1, tracked.Reservation.DisposeCount);
    }

    private static TrackedCapture CreateImageCapture(uint sequence)
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
        return new TrackedCapture(
            new ClipboardCaptureResult(
                sequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Captured,
                [format]),
            lease,
            reservation);
    }

    private static ClipboardEntry ImageEntry(long order) => new(
        Guid.NewGuid(),
        ClipboardContentKind.Image,
        order.ToString("x64"),
        string.Empty,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        order,
        1,
        false,
        1,
        Image: new ClipboardImageMetadata(
            2,
            2,
            true,
            null,
            null,
            new ClipboardAssetReference(new string('a', 64), ClipboardAssetState.Ready, 1),
            new ClipboardAssetReference(new string('b', 64), ClipboardAssetState.Ready, 1)));

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The clipboard controller did not publish in time.");
            }

            await Task.Delay(10, cancellationToken);
        }
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

    private sealed class CountingNormalizer : IClipboardCaptureNormalizer
    {
        public int CallCount { get; private set; }

        public ClipboardNormalizedContent? Normalize(ClipboardCaptureResult capture)
        {
            CallCount++;
            return null;
        }
    }

    private sealed class SequenceTextNormalizer : IClipboardCaptureNormalizer
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ClipboardNormalizedContent? Normalize(ClipboardCaptureResult capture)
        {
            Interlocked.Increment(ref _callCount);
            var value = $"capture-{capture.RuntimeSequence}";
            return new ClipboardTextContent(
                capture.RuntimeSequence.ToString("x64"),
                value,
                value,
                [new ClipboardStoredRepresentation(
                    ClipboardRepresentationKind.UnicodeText,
                    100,
                    value,
                    checked((value.Length * 2) + 2))]);
        }
    }

    private sealed class SequencedImageCaptureService(
        Func<int, Task<ClipboardEntry>> resultFactory)
        : IClipboardImageCaptureService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ClipboardEntry> CaptureAsync(
            ClipboardCaptureResult capture,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            capture.Dispose();
            return resultFactory(call);
        }
    }

    private sealed class BlockingImageCaptureService : IClipboardImageCaptureService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClipboardEntry> CaptureAsync(
            ClipboardCaptureResult capture,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking capture unexpectedly completed.");
            }
            finally
            {
                capture.Dispose();
            }
        }
    }

    private sealed class ControlledImageCaptureService(ClipboardEntry entry)
        : IClipboardImageCaptureService
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClipboardEntry> CaptureAsync(
            ClipboardCaptureResult capture,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return entry;
            }
            finally
            {
                capture.Dispose();
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class TrackingMaintenanceService : IClipboardHistoryMaintenanceService
    {
        public System.Collections.Concurrent.ConcurrentBag<Guid> RemovedEntryIds { get; } = [];

        public Task<ClipboardHistoryMaintenanceResult> ApplyRetentionAsync(
            ClipboardRetentionOptions options,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardHistoryMaintenanceResult(
                [],
                RemainingEntryCount: 1,
                ManagedBytesBefore: 0,
                ManagedBytesAfter: 0,
                PendingAssetDeleteCount: 0,
                AssetCleanupDeferred: false));

        public Task<ClipboardHistoryMaintenanceResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardHistoryMaintenanceResult(
                [],
                RemainingEntryCount: 0,
                ManagedBytesBefore: 0,
                ManagedBytesAfter: 0,
                PendingAssetDeleteCount: 0,
                AssetCleanupDeferred: false));

        public Task<bool> RemoveEntryAsync(
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            RemovedEntryIds.Add(entryId);
            return Task.FromResult(true);
        }
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

    private sealed record TrackedCapture(
        ClipboardCaptureResult Capture,
        TrackingLease Lease,
        TrackingReservation Reservation);
}
