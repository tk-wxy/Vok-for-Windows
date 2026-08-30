using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Persistence;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ClipboardImageAssetCoordinator :
    IClipboardImageAssetService,
    IClipboardImageReplayMaterializer
{
    private static readonly TimeSpan DefaultShutdownWatchdog =
        TimeSpan.FromSeconds(5);

    private readonly BlockingCollection<AssetWorkItem> _queue;
    private readonly IClipboardImageMetadataRepository _repository;
    private readonly IClipboardContentAssetStore _assetStore;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private readonly TimeSpan _shutdownWatchdog;
    private readonly Action<Exception>? _diagnosticFault;
    private readonly int _maximumJobs;
    private readonly int _thumbnailMaximumEdgePixels;
    private readonly int _thumbnailEncodedOverheadBytes;
    private readonly ClipboardImageReplayEncoder _replayEncoder;
    private ClipboardImageTemporaryStore? _replayTemporaryStore;
    private Exception? _terminalFailure;
    private int _outstandingJobs;
    private int _workerManagedThreadId;
    private int _disposed;
    private int _resourcesDisposed;
    private int _shutdownDrainCompleted;

    /// <summary>
    /// Creates the single owner of clipboard asset file transitions. The
    /// coordinator owns <paramref name="assetStore"/> and disposes it; the
    /// shared SQLite repository remains owned by its composition root.
    /// </summary>
    public ClipboardImageAssetCoordinator(
        SqliteClipboardHistoryRepository repository,
        ContentAddressedClipboardAssetStore assetStore,
        ClipboardImageCaptureOptions? options = null,
        Action<Exception>? diagnosticFault = null)
        : this(
            repository,
            assetStore,
            options ?? new ClipboardImageCaptureOptions(),
            DefaultShutdownWatchdog,
            diagnosticFault)
    {
    }

    internal ClipboardImageAssetCoordinator(
        IClipboardImageMetadataRepository repository,
        IClipboardContentAssetStore assetStore,
        ClipboardImageCaptureOptions options,
        TimeSpan shutdownWatchdog,
        Action<Exception>? diagnosticFault = null,
        ClipboardImageTemporaryStore? replayTemporaryStore = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        ArgumentNullException.ThrowIfNull(options);
        if (shutdownWatchdog <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownWatchdog));
        }

        _maximumJobs = options.ImageWorkerCapacity;
        _thumbnailMaximumEdgePixels = options.ThumbnailMaximumEdgePixels;
        _thumbnailEncodedOverheadBytes = options.ThumbnailEncodedOverheadBytes;
        _replayEncoder = new ClipboardImageReplayEncoder(options);
        _replayTemporaryStore = replayTemporaryStore;
        _shutdownWatchdog = shutdownWatchdog;
        _diagnosticFault = diagnosticFault;
        _queue = new BlockingCollection<AssetWorkItem>(_maximumJobs);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Clipboard asset coordinator",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    internal int OutstandingJobCount => Volatile.Read(ref _outstandingJobs);

    internal int WorkerManagedThreadId =>
        Volatile.Read(ref _workerManagedThreadId);

    internal bool IsWorkerThreadAlive => _thread.IsAlive;

    internal int OutstandingReplayLeaseCount =>
        _replayTemporaryStore?.OutstandingLeaseCount ?? 0;

    /// <summary>
    /// Transfers ownership of <paramref name="result"/> on every non-null call.
    /// Successful publication occurs only after both files are verified and the
    /// schema-5 metadata transaction commits.
    /// </summary>
    public Task<ClipboardEntry> CommitAsync(
        ClipboardImageNormalizationResult result,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Enqueue(new CommitWorkItem(
            result,
            capturedUtc,
            cancellationToken,
            ReleaseJobSlot));
    }

    public Task<bool> RemoveAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(entryId, Guid.Empty);
        return Enqueue(new RemoveWorkItem(
            entryId,
            cancellationToken,
            ReleaseJobSlot));
    }

    public Task<ClipboardAssetReference?> VerifyAssetAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Enqueue(new VerifyWorkItem(
            assetHash,
            expectedRevision,
            cancellationToken,
            ReleaseJobSlot));
    }

    public Task<ClipboardImageThumbnailResult?> LoadThumbnailAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Enqueue(new LoadThumbnailWorkItem(
            assetHash,
            expectedRevision,
            cancellationToken,
            ReleaseJobSlot));
    }

    public Task<ClipboardImageReplayMaterialization> MaterializeAsync(
        ClipboardReplayPayload descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Enqueue(new MaterializeReplayWorkItem(
            descriptor,
            cancellationToken,
            ReleaseJobSlot));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _shutdown.Cancel();
        while (_queue.TryTake(out var queued))
        {
            queued.CancelForShutdown();
        }

        Volatile.Write(ref _shutdownDrainCompleted, 1);
        if (!_thread.Join(_shutdownWatchdog))
        {
            ReportFault(new TimeoutException(
                $"The clipboard asset coordinator did not stop within " +
                $"{_shutdownWatchdog.TotalSeconds} seconds. Its background thread " +
                "will release resources when the active file or database call returns."));
            return;
        }

        DisposeResources();
    }

    private Task<T> Enqueue<T>(AssetWorkItem<T> item)
    {
        var terminalFailure = Volatile.Read(ref _terminalFailure);
        if (Volatile.Read(ref _disposed) != 0 || terminalFailure is not null)
        {
            item.Reject(terminalFailure is null
                ? new ObjectDisposedException(nameof(ClipboardImageAssetCoordinator))
                : new InvalidOperationException(
                    "The clipboard asset coordinator is unavailable.",
                    terminalFailure));
            return item.Task;
        }

        if (!TryAcquireJobSlot())
        {
            item.Reject(new InvalidOperationException(
                "The clipboard asset coordinator has reached its bounded capacity."));
            return item.Task;
        }

        item.MarkSlotAcquired();
        item.ActivateCancellation();
        if (item.IsTerminal)
        {
            return item.Task;
        }

        terminalFailure = Volatile.Read(ref _terminalFailure);
        if (Volatile.Read(ref _disposed) != 0 || terminalFailure is not null)
        {
            item.Reject(terminalFailure is null
                ? new ObjectDisposedException(nameof(ClipboardImageAssetCoordinator))
                : new InvalidOperationException(
                    "The clipboard asset coordinator is unavailable.",
                    terminalFailure));
            return item.Task;
        }

        try
        {
            if (!_queue.TryAdd(item))
            {
                item.Reject(new InvalidOperationException(
                    "The clipboard asset coordinator queue is full."));
            }
        }
        catch (InvalidOperationException exception)
        {
            item.Reject(Volatile.Read(ref _disposed) != 0
                ? new ObjectDisposedException(
                    nameof(ClipboardImageAssetCoordinator),
                    exception)
                : new InvalidOperationException(
                    "The clipboard asset coordinator queue is not accepting work.",
                    exception));
        }

        return item.Task;
    }

    private void Run()
    {
        Volatile.Write(
            ref _workerManagedThreadId,
            Environment.CurrentManagedThreadId);
        try
        {
            RecoverPendingDeletes(_shutdown.Token);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item.Execute(this, _shutdown.Token);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _terminalFailure, exception);
            try
            {
                _queue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }

            while (_queue.TryTake(out var queued))
            {
                queued.Reject(new InvalidOperationException(
                    "The clipboard asset coordinator terminated unexpectedly.",
                    exception));
            }
        }
        finally
        {
            Volatile.Write(ref _workerManagedThreadId, 0);
            if (Volatile.Read(ref _disposed) != 0 &&
                Volatile.Read(ref _shutdownDrainCompleted) != 0)
            {
                DisposeResources();
            }
        }
    }

    private ClipboardEntry CommitCore(
        ClipboardImageNormalizationResult result,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        ClipboardPreparedAsset primary;
        using (var stream = result.OpenCanonicalPngReadStream())
        {
            primary = _assetStore.CommitPng(
                stream,
                result.CanonicalPngByteLength,
                result.PixelWidth,
                result.PixelHeight,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ClipboardPreparedAsset thumbnail;
        using (var stream = result.OpenThumbnailPngReadStream())
        {
            thumbnail = _assetStore.CommitPng(
                stream,
                result.ThumbnailPngByteLength,
                result.ThumbnailPixelWidth,
                result.ThumbnailPixelHeight,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var upsert = _repository.UpsertImageAsync(
                new ClipboardImageContent(
                    result.SemanticHash,
                    result.PixelWidth,
                    result.PixelHeight,
                    result.HasAlpha,
                    result.DpiXMilli,
                    result.DpiYMilli,
                    primary,
                    thumbnail),
                capturedUtc,
                cancellationToken)
            .GetAwaiter()
            .GetResult();

        ProcessDeleteClaims(upsert.PendingDeletes);
        return upsert.Entry;
    }

    private bool RemoveCore(Guid entryId, CancellationToken cancellationToken)
    {
        var result = _repository.RemoveWithAssetClaimsAsync(
                entryId,
                cancellationToken)
            .GetAwaiter()
            .GetResult();
        ProcessDeleteClaims(result.PendingDeletes);
        return result.Removed;
    }

    private ClipboardAssetReference? VerifyAssetCore(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var stored = _repository.LoadAssetAsync(assetHash, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (stored is null)
        {
            return null;
        }

        var current = new ClipboardAssetReference(
            stored.Asset.Hash,
            stored.State,
            stored.Revision);
        if (stored.Revision != expectedRevision ||
            stored.State is not ClipboardAssetState.Ready)
        {
            return current;
        }

        try
        {
            _assetStore.ValidatePng(stored.Asset, cancellationToken);
            return current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFault(new InvalidDataException(
                "A clipboard image asset failed integrity validation and was quarantined.",
                exception));
            return _repository.MarkAssetQuarantinedAsync(
                    assetHash,
                    expectedRevision,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }

    private ClipboardImageThumbnailResult? LoadThumbnailCore(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var stored = _repository.LoadAssetAsync(assetHash, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (stored is null)
        {
            return null;
        }

        if (stored.Revision != expectedRevision ||
            stored.State is not ClipboardAssetState.Ready)
        {
            return CreateThumbnailResult(stored, pngBytes: null);
        }

        try
        {
            var decodedBytes = checked(
                (long)stored.Asset.PixelWidth * stored.Asset.PixelHeight * 4);
            var maximumEncodedBytes = checked(
                decodedBytes + _thumbnailEncodedOverheadBytes);
            if (stored.Asset.PixelWidth > _thumbnailMaximumEdgePixels ||
                stored.Asset.PixelHeight > _thumbnailMaximumEdgePixels ||
                stored.Asset.ByteLength > maximumEncodedBytes)
            {
                throw new InvalidDataException(
                    "The clipboard thumbnail exceeds its bounded presentation envelope.");
            }

            var pngBytes = _assetStore.ReadPng(stored.Asset, cancellationToken);
            return CreateThumbnailResult(stored, pngBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFault(new InvalidDataException(
                "A clipboard thumbnail failed integrity validation and was quarantined.",
                exception));
            var current = _repository.MarkAssetQuarantinedAsync(
                    assetHash,
                    expectedRevision,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return current is null
                ? null
                : new ClipboardImageThumbnailResult(
                    current.Hash,
                    current.State,
                    current.Revision,
                    stored.Asset.PixelWidth,
                    stored.Asset.PixelHeight,
                    pngBytes: null);
        }
    }

    private ClipboardImageReplayMaterialization MaterializeReplayCore(
        ClipboardReplayPayload descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.Kind is not ClipboardContentKind.Image ||
            descriptor.Image?.PrimaryPng is not
            {
                State: ClipboardAssetState.Ready,
                Revision: >= 1,
            } primary)
        {
            throw new InvalidDataException(
                "The clipboard image replay descriptor has no Ready primary asset.");
        }

        var stored = _repository.LoadAssetAsync(primary.Hash, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (stored is null ||
            stored.Revision != primary.Revision ||
            stored.State is not ClipboardAssetState.Ready)
        {
            throw new InvalidDataException(
                "The clipboard image primary asset changed before replay.");
        }


        if (descriptor.Image is not { } image ||
            stored.Asset.PixelWidth != image.PixelWidth ||
            stored.Asset.PixelHeight != image.PixelHeight)
        {
            throw new InvalidDataException(
                "The clipboard image replay descriptor dimensions do not match its primary asset.");
        }

        IClipboardPayloadLease canonicalPng;
        try
        {
            canonicalPng = _assetStore.OpenVerifiedPng(
                stored.Asset,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            QuarantineReplayAsset(primary, exception);
            throw new InvalidDataException(
                "The clipboard image primary asset failed replay validation.",
                exception);
        }

        IClipboardPayloadLease? ownedCanonical = canonicalPng;
        try
        {
            var temporaryStore = GetReplayTemporaryStore();
            var transferredCanonical = ownedCanonical;
            ownedCanonical = null;
            return _replayEncoder.Materialize(
                descriptor,
                transferredCanonical,
                temporaryStore,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ClipboardImageReplayIntegrityException exception)
        {
            QuarantineReplayAsset(primary, exception);
            throw new InvalidDataException(
                "The clipboard image primary pixels failed replay validation.",
                exception);
        }
        finally
        {
            ownedCanonical?.Dispose();
        }
    }

    private ClipboardImageTemporaryStore GetReplayTemporaryStore() =>
        _replayTemporaryStore ??= ClipboardImageTemporaryStore.CreateDefault(
            ReportFault);

    private void QuarantineReplayAsset(
        ClipboardAssetReference primary,
        Exception exception)
    {
        ReportFault(new InvalidDataException(
            "A clipboard image primary asset failed replay integrity validation and was quarantined.",
            exception));
        _ = _repository.MarkAssetQuarantinedAsync(
                primary.Hash,
                primary.Revision,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static ClipboardImageThumbnailResult CreateThumbnailResult(
        ClipboardStoredAsset stored,
        byte[]? pngBytes) =>
        new(
            stored.Asset.Hash,
            stored.State,
            stored.Revision,
            stored.Asset.PixelWidth,
            stored.Asset.PixelHeight,
            pngBytes);

    private void RecoverPendingDeletes(CancellationToken cancellationToken)
    {
        var claims = _repository.LoadPendingAssetDeletesAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        ProcessDeleteClaims(claims, cancellationToken);
    }

    private void ProcessDeleteClaims(
        IReadOnlyList<ClipboardAssetDeleteClaim> claims,
        CancellationToken cancellationToken = default)
    {
        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isCurrent;
            try
            {
                isCurrent = _repository.IsAssetDeleteClaimCurrentAsync(
                        claim,
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReportFault(exception);
                continue;
            }

            if (!isCurrent)
            {
                continue;
            }

            try
            {
                _assetStore.Delete(claim);
            }
            catch (Exception exception)
            {
                ReportFault(exception);
                continue;
            }

            try
            {
                if (!_repository.CompleteAssetDeleteAsync(
                        claim,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult())
                {
                    QuarantineUnexpectedDeleteRace(claim);
                }
            }
            catch (Exception exception)
            {
                // The file deletion is idempotent. Keeping the tombstone lets
                // startup recovery finish the database half after a crash/fault.
                ReportFault(exception);
            }
        }
    }

    private void QuarantineUnexpectedDeleteRace(ClipboardAssetDeleteClaim claim)
    {
        var stored = _repository.LoadAssetAsync(claim.Hash, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (stored is not null && stored.State is ClipboardAssetState.Ready)
        {
            _ = _repository.MarkAssetQuarantinedAsync(
                    claim.Hash,
                    stored.Revision,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        ReportFault(new InvalidOperationException(
            "A clipboard asset deletion claim changed after its file was removed; " +
            "the surviving metadata was quarantined."));
    }

    private bool TryAcquireJobSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _outstandingJobs);
            if (current >= _maximumJobs)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _outstandingJobs,
                    current + 1,
                    current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseJobSlot() =>
        Interlocked.Decrement(ref _outstandingJobs);

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            _replayTemporaryStore?.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _assetStore.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            _queue.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            _shutdown.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ReportFault(new InvalidOperationException(
                "The clipboard asset coordinator did not release every owned resource cleanly.",
                failure));
        }
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            if (_diagnosticFault is null)
            {
                Trace.TraceWarning(
                    "Clipboard asset coordinator fault: {0}",
                    exception);
            }
            else
            {
                _diagnosticFault(exception);
            }
        }
        catch
        {
            // Diagnostics cannot alter asset state transitions.
        }
    }

    private abstract class AssetWorkItem
    {
        private readonly CancellationToken _callerCancellation;
        private CancellationTokenRegistration _cancellationRegistration;
        private readonly Action _releaseSlot;
        private int _claim;
        private int _terminalPublished;
        private int _slotAcquired;

        protected AssetWorkItem(
            CancellationToken callerCancellation,
            Action releaseSlot)
        {
            _callerCancellation = callerCancellation;
            _releaseSlot = releaseSlot;
        }

        public bool IsTerminal => Volatile.Read(ref _claim) == 2;

        public void MarkSlotAcquired() =>
            Volatile.Write(ref _slotAcquired, 1);

        public void ActivateCancellation() =>
            _cancellationRegistration = _callerCancellation.Register(
                static state => ((AssetWorkItem)state!).CancelBeforeClaim(),
                this);

        public void Execute(
            ClipboardImageAssetCoordinator owner,
            CancellationToken shutdownToken)
        {
            if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _callerCancellation,
                    shutdownToken);
                ExecuteCore(owner, linkedCancellation.Token);
            }
            catch (Exception exception)
            {
                PublishFailure(exception);
            }
            finally
            {
                try
                {
                    ReleaseOwnedPayload();
                }
                catch (Exception exception)
                {
                    owner.ReportFault(new IOException(
                        "A clipboard asset work item could not release its owned payload.",
                        exception));
                }

                PublishTerminal();
            }
        }

        public void Reject(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            var failure = ReleaseOwnedPayload(exception);
            PublishFailure(failure ?? exception);
            PublishTerminal();
        }

        public void CancelForShutdown()
        {
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            var failure = ReleaseOwnedPayload(failure: null);
            if (failure is null)
            {
                PublishCancellation(new CancellationToken(canceled: true));
            }
            else
            {
                PublishFailure(failure);
            }

            PublishTerminal();
        }

        protected abstract void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken);

        protected abstract void PublishFailure(Exception exception);

        protected abstract void PublishCancellation(CancellationToken cancellationToken);

        protected virtual void ReleaseOwnedPayload()
        {
        }

        private void CancelBeforeClaim()
        {
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0)
            {
                return;
            }

            var failure = ReleaseOwnedPayload(failure: null);
            if (failure is null)
            {
                PublishCancellation(_callerCancellation);
            }
            else
            {
                PublishFailure(failure);
            }

            PublishTerminal();
        }

        private Exception? ReleaseOwnedPayload(Exception? failure)
        {
            try
            {
                ReleaseOwnedPayload();
            }
            catch (Exception exception)
            {
                return failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            return failure;
        }

        private void PublishTerminal()
        {
            if (Interlocked.Exchange(ref _terminalPublished, 1) == 0 &&
                Volatile.Read(ref _slotAcquired) != 0)
            {
                _releaseSlot();
            }
        }
    }

    private abstract class AssetWorkItem<T>(
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem(callerCancellation, releaseSlot)
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        protected bool PublishSuccess(T result) =>
            _completion.TrySetResult(result);

        protected override void PublishFailure(Exception exception)
        {
            if (exception is OperationCanceledException cancellation)
            {
                _completion.TrySetCanceled(cancellation.CancellationToken);
            }
            else
            {
                _completion.TrySetException(exception);
            }
        }

        protected override void PublishCancellation(CancellationToken cancellationToken) =>
            _completion.TrySetCanceled(cancellationToken);
    }

    private sealed class CommitWorkItem(
        ClipboardImageNormalizationResult result,
        DateTimeOffset capturedUtc,
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem<ClipboardEntry>(callerCancellation, releaseSlot)
    {
        private ClipboardImageNormalizationResult? _result = result;

        protected override void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken)
        {
            var current = Volatile.Read(ref _result) ??
                throw new ObjectDisposedException(nameof(ClipboardImageNormalizationResult));
            PublishSuccess(owner.CommitCore(current, capturedUtc, cancellationToken));
        }

        protected override void ReleaseOwnedPayload() =>
            Interlocked.Exchange(ref _result, null)?.Dispose();
    }

    private sealed class RemoveWorkItem(
        Guid entryId,
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem<bool>(callerCancellation, releaseSlot)
    {
        protected override void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken) =>
            PublishSuccess(owner.RemoveCore(entryId, cancellationToken));
    }

    private sealed class VerifyWorkItem(
        string assetHash,
        long expectedRevision,
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem<ClipboardAssetReference?>(callerCancellation, releaseSlot)
    {
        protected override void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken) =>
            PublishSuccess(owner.VerifyAssetCore(
                assetHash,
                expectedRevision,
                cancellationToken));
    }

    private sealed class LoadThumbnailWorkItem(
        string assetHash,
        long expectedRevision,
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem<ClipboardImageThumbnailResult?>(callerCancellation, releaseSlot)
    {
        protected override void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken) =>
            PublishSuccess(owner.LoadThumbnailCore(
                assetHash,
                expectedRevision,
                cancellationToken));
    }

    private sealed class MaterializeReplayWorkItem(
        ClipboardReplayPayload descriptor,
        CancellationToken callerCancellation,
        Action releaseSlot)
        : AssetWorkItem<ClipboardImageReplayMaterialization>(
            callerCancellation,
            releaseSlot)
    {
        protected override void ExecuteCore(
            ClipboardImageAssetCoordinator owner,
            CancellationToken cancellationToken)
        {
            var materialization = owner.MaterializeReplayCore(
                descriptor,
                cancellationToken);
            if (!PublishSuccess(materialization))
            {
                materialization.Dispose();
            }
        }
    }
}
