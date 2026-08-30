using System.Runtime.ExceptionServices;

namespace Vok.Core.Clipboard;

public sealed class ClipboardImageReplayMaterialization : IDisposable
{
    private IClipboardPayloadLease? _canonicalPng;
    private IClipboardPayloadLease? _dibV5;
    private int _disposed;

    public ClipboardImageReplayMaterialization(
        string semanticHash,
        int pixelWidth,
        int pixelHeight,
        bool hasAlpha,
        int? dpiXMilli,
        int? dpiYMilli,
        IClipboardPayloadLease canonicalPng,
        IClipboardPayloadLease dibV5)
    {
        if (!IsLowercaseSha256(semanticHash))
        {
            throw new ArgumentException(
                "The clipboard image semantic hash must be a lowercase SHA-256 value.",
                nameof(semanticHash));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        if (dpiXMilli is <= 0 || dpiYMilli is <= 0 ||
            dpiXMilli.HasValue != dpiYMilli.HasValue)
        {
            throw new ArgumentException(
                "Clipboard image DPI metadata must be absent or contain two positive values.");
        }

        ArgumentNullException.ThrowIfNull(canonicalPng);
        ArgumentNullException.ThrowIfNull(dibV5);
        if (canonicalPng.Length < 1)
        {
            throw new ArgumentException(
                "The canonical PNG lease must not be empty.",
                nameof(canonicalPng));
        }

        var expectedDibLength = checked(
            124L + (long)pixelWidth * pixelHeight * 4);
        if (dibV5.Length != expectedDibLength)
        {
            throw new ArgumentException(
                "The DIBV5 lease length does not match its pixel dimensions.",
                nameof(dibV5));
        }

        SemanticHash = semanticHash;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        HasAlpha = hasAlpha;
        DpiXMilli = dpiXMilli;
        DpiYMilli = dpiYMilli;
        _canonicalPng = canonicalPng;
        _dibV5 = dibV5;
    }

    public string SemanticHash { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool HasAlpha { get; }

    public int? DpiXMilli { get; }

    public int? DpiYMilli { get; }

    public long CanonicalPngByteLength => GetLease(_canonicalPng).Length;

    public long DibV5ByteLength => GetLease(_dibV5).Length;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Stream OpenCanonicalPngReadStream() => GetLease(_canonicalPng).OpenRead();

    public Stream OpenDibV5ReadStream() => GetLease(_dibV5).OpenRead();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        DisposeOne(Interlocked.Exchange(ref _dibV5, null), ref failure);
        DisposeOne(Interlocked.Exchange(ref _canonicalPng, null), ref failure);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private IClipboardPayloadLease GetLease(IClipboardPayloadLease? lease)
    {
        ObjectDisposedException.ThrowIf(IsDisposed || lease is null, this);
        return lease;
    }

    private static void DisposeOne(IDisposable? value, ref Exception? failure)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
    }

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public interface IClipboardImageReplayMaterializer : IDisposable
{
    Task<ClipboardImageReplayMaterialization> MaterializeAsync(
        ClipboardReplayPayload descriptor,
        CancellationToken cancellationToken = default);
}
