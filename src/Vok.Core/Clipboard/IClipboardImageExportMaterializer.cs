using System.Runtime.ExceptionServices;

namespace Vok.Core.Clipboard;

public sealed class ClipboardImageExportMaterialization : IDisposable
{
    private IClipboardPayloadLease? _canonicalPng;
    private int _disposed;

    public ClipboardImageExportMaterialization(
        string assetHash,
        string semanticHash,
        int pixelWidth,
        int pixelHeight,
        bool hasAlpha,
        IClipboardPayloadLease canonicalPng)
    {
        if (!IsLowercaseSha256(assetHash))
        {
            throw new ArgumentException(
                "The clipboard image asset hash must be a lowercase SHA-256 value.",
                nameof(assetHash));
        }

        if (!IsLowercaseSha256(semanticHash))
        {
            throw new ArgumentException(
                "The clipboard image semantic hash must be a lowercase SHA-256 value.",
                nameof(semanticHash));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentNullException.ThrowIfNull(canonicalPng);
        if (canonicalPng.Length < 1)
        {
            throw new ArgumentException(
                "The canonical PNG lease must not be empty.",
                nameof(canonicalPng));
        }

        AssetHash = assetHash;
        SemanticHash = semanticHash;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        HasAlpha = hasAlpha;
        _canonicalPng = canonicalPng;
    }

    public string AssetHash { get; }

    public string SemanticHash { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool HasAlpha { get; }

    public long CanonicalPngByteLength => GetLease().Length;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Stream OpenCanonicalPngReadStream() => GetLease().OpenRead();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _canonicalPng, null)?.Dispose();
        }
        catch (Exception exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private IClipboardPayloadLease GetLease()
    {
        var lease = _canonicalPng;
        ObjectDisposedException.ThrowIf(IsDisposed || lease is null, this);
        return lease;
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IClipboardImageExportMaterializer : IDisposable
{
    Task<ClipboardImageExportMaterialization> MaterializeForExportAsync(
        ClipboardReplayPayload descriptor,
        CancellationToken cancellationToken = default);
}
