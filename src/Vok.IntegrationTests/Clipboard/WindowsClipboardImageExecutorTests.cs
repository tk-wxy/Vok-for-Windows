using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardImageExecutorTests
{
    [Fact]
    public async Task SuccessfulJob_RunsOnDedicatedMtaAndReleasesInputBeforePublishingResult()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 2);
        using var harness = new ExecutorHarness(options);
        var recording = new RecordingNormalizer(new ClipboardImageNormalizer(options));
        using var executor = harness.CreateExecutor(recording);
        var owned = CreateOwnedPngCapture();

        using var result = await executor.NormalizeAsync(
            owned.Capture,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(Environment.CurrentManagedThreadId, recording.ManagedThreadId);
        Assert.Equal(ApartmentState.MTA, recording.ApartmentState);
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
        Assert.Equal(2, executor.OutstandingTemporaryLeaseCount);
        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);

        result.Dispose();
        Assert.Equal(0, executor.OutstandingTemporaryLeaseCount);
    }

    [Fact]
    public async Task PreCancelledJob_NeverReachesNormalizerAndReleasesOwnership()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 1);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        using var executor = harness.CreateExecutor(normalizer);
        var owned = CreateOwnedPngCapture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var task = executor.NormalizeAsync(owned.Capture, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, normalizer.CallCount);
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task MissingExactReservation_IsRejectedBeforeQueueAdmission()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 1);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        using var executor = harness.CreateExecutor(normalizer);
        var owned = CreateOwnedPngCapture();
        owned.Reservation.OverrideReservedBytes(owned.Reservation.ReservedBytes - 1);

        var task = executor.NormalizeAsync(
            owned.Capture,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => task);
        Assert.Equal(0, normalizer.CallCount);
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task CapacityCountsActiveAndQueuedJobsAndRejectsThirdOwnership()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 2);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        using var executor = harness.CreateExecutor(normalizer);
        var first = CreateOwnedPngCapture();
        var second = CreateOwnedPngCapture();
        var third = CreateOwnedPngCapture();

        var testCancellation = TestContext.Current.CancellationToken;
        var firstTask = executor.NormalizeAsync(first.Capture, testCancellation);
        Assert.True(normalizer.Started.Wait(TimeSpan.FromSeconds(5), testCancellation));
        var secondTask = executor.NormalizeAsync(second.Capture, testCancellation);
        var thirdTask = executor.NormalizeAsync(third.Capture, testCancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => thirdTask);
        Assert.True(third.Capture.IsDisposed);
        Assert.True(third.Reservation.IsDisposed);
        Assert.Equal(2, executor.OutstandingJobCount);

        normalizer.Release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondTask);
        Assert.True(first.Capture.IsDisposed);
        Assert.True(second.Capture.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task QueuedCancellation_WinsClaimOnceAndDoesNotReachNormalizer()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 2);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        using var executor = harness.CreateExecutor(normalizer);
        var active = CreateOwnedPngCapture();
        var queued = CreateOwnedPngCapture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var activeTask = executor.NormalizeAsync(
            active.Capture,
            TestContext.Current.CancellationToken);
        Assert.True(normalizer.Started.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        var queuedTask = executor.NormalizeAsync(queued.Capture, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);
        Assert.True(queued.Capture.IsDisposed);
        Assert.True(queued.Reservation.IsDisposed);
        Assert.Equal(1, normalizer.CallCount);
        Assert.Equal(1, executor.OutstandingJobCount);

        normalizer.Release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => activeTask);
        Assert.Equal(1, normalizer.CallCount);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task ActiveCancellation_IsObservedByWorkerAndReleasesCapture()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 1);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        using var executor = harness.CreateExecutor(normalizer);
        var owned = CreateOwnedPngCapture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var task = executor.NormalizeAsync(owned.Capture, cancellation.Token);
        Assert.True(normalizer.Started.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task Dispose_CancelsActiveAndQueuedJobsAndStopsWorkerWithoutDoubleTerminal()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 2);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        var executor = harness.CreateExecutor(normalizer);
        var active = CreateOwnedPngCapture();
        var queued = CreateOwnedPngCapture();

        var testCancellation = TestContext.Current.CancellationToken;
        var activeTask = executor.NormalizeAsync(active.Capture, testCancellation);
        Assert.True(normalizer.Started.Wait(TimeSpan.FromSeconds(5), testCancellation));
        var queuedTask = executor.NormalizeAsync(queued.Capture, testCancellation);

        executor.Dispose();
        executor.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);
        Assert.True(active.Capture.IsDisposed);
        Assert.True(queued.Capture.IsDisposed);
        Assert.True(active.Reservation.IsDisposed);
        Assert.True(queued.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
        Assert.Equal(0, executor.OutstandingTemporaryLeaseCount);
        Assert.False(executor.IsWorkerThreadAlive);
        Assert.Equal(0, executor.WorkerManagedThreadId);
    }

    [Fact]
    public async Task SubmissionAfterDispose_IsRejectedAndStillConsumesCaptureOwnership()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 1);
        using var harness = new ExecutorHarness(options);
        var normalizer = new BlockingNormalizer();
        var executor = harness.CreateExecutor(normalizer);
        executor.Dispose();
        var owned = CreateOwnedPngCapture();

        var task = executor.NormalizeAsync(
            owned.Capture,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
    }

    [Fact]
    public async Task ShutdownWatchdog_DefersCleanupUntilUninterruptibleCodecReturns()
    {
        var options = new ClipboardImageCaptureOptions(imageWorkerCapacity: 1);
        using var harness = new ExecutorHarness(options);
        var normalizer = new IgnoringCancellationNormalizer();
        var executor = harness.CreateExecutor(
            normalizer,
            TimeSpan.FromMilliseconds(50));
        var owned = CreateOwnedPngCapture();
        var task = executor.NormalizeAsync(
            owned.Capture,
            TestContext.Current.CancellationToken);
        Assert.True(normalizer.Started.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));

        executor.Dispose();

        Assert.True(executor.IsWorkerThreadAlive);
        Assert.False(owned.Capture.IsDisposed);
        normalizer.Release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.True(SpinWait.SpinUntil(
            () => !executor.IsWorkerThreadAlive,
            TimeSpan.FromSeconds(5)));
        Assert.True(owned.Capture.IsDisposed);
        Assert.True(owned.Reservation.IsDisposed);
        Assert.Equal(0, executor.OutstandingJobCount);
        Assert.Equal(0, executor.OutstandingTemporaryLeaseCount);
    }

    private static OwnedCapture CreateOwnedPngCapture()
    {
        var pixels = new byte[]
        {
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            255, 255, 255, 255,
        };
        var source = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        var payload = encoded.ToArray();
        var reservation = new TrackingReservation(payload.Length + 16);
        var capture = new ClipboardCaptureResult(
            RuntimeSequence: 1,
            CapturedUtc: DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Captured,
            [
                new ClipboardFormatSnapshot(
                    SourceOrder: 0,
                    FormatName: "PNG",
                    ClipboardRepresentationKind.Png,
                    ClipboardFormatRole.Content,
                    ClipboardFormatCaptureState.Captured,
                    payload,
                    SourceByteLength: payload.Length,
                    ImageHeader: new ClipboardImageHeader(2, 2, 16),
                    Reservation: reservation),
            ]);
        return new OwnedCapture(capture, reservation);
    }

    private sealed record OwnedCapture(
        ClipboardCaptureResult Capture,
        TrackingReservation Reservation);

    private sealed class TrackingReservation(long reservedBytes)
        : IClipboardPayloadReservation
    {
        private int _disposed;
        private long _reservedBytes = reservedBytes;

        public long ReservedBytes => Volatile.Read(ref _reservedBytes);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void OverrideReservedBytes(long value) =>
            Volatile.Write(ref _reservedBytes, value);

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class RecordingNormalizer(IClipboardImageNormalizationEngine inner)
        : IClipboardImageNormalizationEngine
    {
        public int ManagedThreadId { get; private set; }

        public ApartmentState ApartmentState { get; private set; }

        public ClipboardImageNormalizationResult Normalize(
            ClipboardCaptureResult capture,
            ClipboardImageTemporaryStore temporaryStore,
            CancellationToken cancellationToken)
        {
            ManagedThreadId = Environment.CurrentManagedThreadId;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
            return inner.Normalize(capture, temporaryStore, cancellationToken);
        }
    }

    private sealed class BlockingNormalizer : IClipboardImageNormalizationEngine
    {
        private int _callCount;

        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public ClipboardImageNormalizationResult Normalize(
            ClipboardCaptureResult capture,
            ClipboardImageTemporaryStore temporaryStore,
            CancellationToken cancellationToken)
        {
            _ = capture;
            _ = temporaryStore;
            Interlocked.Increment(ref _callCount);
            Started.Set();
            Release.Wait(cancellationToken);
            throw new InvalidOperationException("Controlled normalizer completion.");
        }
    }

    private sealed class IgnoringCancellationNormalizer
        : IClipboardImageNormalizationEngine
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public ClipboardImageNormalizationResult Normalize(
            ClipboardCaptureResult capture,
            ClipboardImageTemporaryStore temporaryStore,
            CancellationToken cancellationToken)
        {
            _ = capture;
            _ = temporaryStore;
            _ = cancellationToken;
            Started.Set();
            Release.WaitHandle.WaitOne();
            throw new InvalidOperationException("Controlled uninterruptible codec completion.");
        }
    }

    private sealed class ExecutorHarness : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Vok.ImageExecutorTests",
            Guid.NewGuid().ToString("N"));
        private ClipboardImageTemporaryStore? _store;

        public ExecutorHarness(ClipboardImageCaptureOptions options)
        {
            Options = options;
        }

        public ClipboardImageCaptureOptions Options { get; }

        public WindowsClipboardImageExecutor CreateExecutor(
            IClipboardImageNormalizationEngine normalizer,
            TimeSpan? shutdownWatchdog = null)
        {
            _store = new ClipboardImageTemporaryStore(_root);
            return new WindowsClipboardImageExecutor(
                Options,
                normalizer,
                _store,
                shutdownWatchdog ?? TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            _store?.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
