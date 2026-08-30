using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

internal sealed record ClipboardThumbnailPresentation(
    string AssetHash,
    ClipboardAssetState? State,
    long Revision,
    BitmapSource? Image)
{
    public bool IsReady => State is ClipboardAssetState.Ready && Image is not null;
}

internal interface IClipboardThumbnailCache
{
    Task<ClipboardThumbnailPresentation> LoadAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

internal sealed class ClipboardThumbnailCache : IClipboardThumbnailCache, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IClipboardImageAssetService _assetService;
    private readonly long _maximumDecodedBytes;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = new();
    private long _cachedDecodedBytes;
    private int _disposed;

    public ClipboardThumbnailCache(
        Dispatcher dispatcher,
        IClipboardImageAssetService assetService,
        ClipboardImageCaptureOptions? options = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        _maximumDecodedBytes = (options ?? new ClipboardImageCaptureOptions())
            .ThumbnailCacheBytes;
    }

    internal long CachedDecodedBytes => _cachedDecodedBytes;

    internal int Count => _entries.Count;

    public async Task<ClipboardThumbnailPresentation> LoadAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);

        if (TryGet(assetHash, out var cached))
        {
            return new ClipboardThumbnailPresentation(
                assetHash,
                ClipboardAssetState.Ready,
                expectedRevision,
                cached);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await _loadGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(true);
        try
        {
            _dispatcher.VerifyAccess();
            linkedCancellation.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (TryGet(assetHash, out cached))
            {
                return new ClipboardThumbnailPresentation(
                    assetHash,
                    ClipboardAssetState.Ready,
                    expectedRevision,
                    cached);
            }

            var result = await _assetService
                .LoadThumbnailAsync(
                    assetHash,
                    expectedRevision,
                    linkedCancellation.Token)
                .ConfigureAwait(true);
            _dispatcher.VerifyAccess();
            linkedCancellation.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);

            if (result is null)
            {
                return new ClipboardThumbnailPresentation(
                    assetHash,
                    State: null,
                    expectedRevision,
                    Image: null);
            }

            if (!result.IsReady ||
                !string.Equals(result.AssetHash, assetHash, StringComparison.Ordinal) ||
                result.Revision != expectedRevision)
            {
                return new ClipboardThumbnailPresentation(
                    result.AssetHash,
                    result.State,
                    result.Revision,
                    Image: null);
            }

            var image = Decode(result);
            Add(assetHash, image, result.DecodedByteLength);
            return new ClipboardThumbnailPresentation(
                assetHash,
                ClipboardAssetState.Ready,
                expectedRevision,
                image);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _entries.Clear();
        _leastRecentlyUsed.Clear();
        _cachedDecodedBytes = 0;
        _lifetime.Dispose();
    }

    private bool TryGet(string assetHash, out BitmapSource image)
    {
        if (!_entries.TryGetValue(assetHash, out var node))
        {
            image = null!;
            return false;
        }

        _leastRecentlyUsed.Remove(node);
        _leastRecentlyUsed.AddFirst(node);
        image = node.Value.Image;
        return true;
    }

    private void Add(string assetHash, BitmapSource image, long decodedByteLength)
    {
        if (decodedByteLength > _maximumDecodedBytes)
        {
            return;
        }

        if (_entries.TryGetValue(assetHash, out var existing))
        {
            _leastRecentlyUsed.Remove(existing);
            _cachedDecodedBytes -= existing.Value.DecodedByteLength;
            _entries.Remove(assetHash);
        }

        var node = _leastRecentlyUsed.AddFirst(
            new CacheEntry(assetHash, image, decodedByteLength));
        _entries.Add(assetHash, node);
        _cachedDecodedBytes += decodedByteLength;

        while (_cachedDecodedBytes > _maximumDecodedBytes &&
               _leastRecentlyUsed.Last is { } last)
        {
            _leastRecentlyUsed.RemoveLast();
            _entries.Remove(last.Value.AssetHash);
            _cachedDecodedBytes -= last.Value.DecodedByteLength;
        }
    }

    private static BitmapSource Decode(ClipboardImageThumbnailResult result)
    {
        using var stream = result.OpenPngReadStream();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        if (image.PixelWidth != result.PixelWidth ||
            image.PixelHeight != result.PixelHeight)
        {
            throw new InvalidDataException(
                "The decoded clipboard thumbnail dimensions changed after verification.");
        }

        image.Freeze();
        return image;
    }

    private sealed record CacheEntry(
        string AssetHash,
        BitmapSource Image,
        long DecodedByteLength);
}
