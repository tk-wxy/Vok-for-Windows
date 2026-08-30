namespace Vok.Core.Clipboard;

public sealed class ClipboardImageThumbnailResult
{
    private readonly byte[]? _pngBytes;

    public ClipboardImageThumbnailResult(
        string assetHash,
        ClipboardAssetState state,
        long revision,
        int pixelWidth,
        int pixelHeight,
        byte[]? pngBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);

        if (pngBytes is { Length: 0 } ||
            state is not ClipboardAssetState.Ready && pngBytes is not null)
        {
            throw new ArgumentException(
                "An unavailable clipboard thumbnail cannot expose PNG bytes.",
                nameof(pngBytes));
        }

        AssetHash = assetHash;
        State = state;
        Revision = revision;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _pngBytes = pngBytes?.ToArray();
    }

    public string AssetHash { get; }

    public ClipboardAssetState State { get; }

    public long Revision { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public long DecodedByteLength => checked((long)PixelWidth * PixelHeight * 4);

    public bool IsReady => State is ClipboardAssetState.Ready && _pngBytes is not null;

    public Stream OpenPngReadStream()
    {
        if (_pngBytes is null)
        {
            throw new InvalidOperationException(
                "The clipboard thumbnail is not available for decoding.");
        }

        return new MemoryStream(_pngBytes, writable: false);
    }
}

public interface IClipboardImageAssetService : IDisposable
{
    Task<ClipboardImageThumbnailResult?> LoadThumbnailAsync(
        string assetHash,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);
}
