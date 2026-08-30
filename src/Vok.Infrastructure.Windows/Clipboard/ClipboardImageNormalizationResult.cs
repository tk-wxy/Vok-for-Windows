using System.Runtime.ExceptionServices;
using System.IO;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ClipboardImageNormalizationResult : IDisposable
{
    private IClipboardPayloadLease? _canonicalPng;
    private IClipboardPayloadLease? _thumbnailPng;
    private int _disposed;

    internal ClipboardImageNormalizationResult(
        ClipboardRepresentationKind sourceKind,
        string semanticHash,
        int pixelWidth,
        int pixelHeight,
        bool hasAlpha,
        int? dpiXMilli,
        int? dpiYMilli,
        int thumbnailPixelWidth,
        int thumbnailPixelHeight,
        IClipboardPayloadLease canonicalPng,
        IClipboardPayloadLease thumbnailPng)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thumbnailPixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thumbnailPixelHeight, 1);
        ArgumentNullException.ThrowIfNull(canonicalPng);
        ArgumentNullException.ThrowIfNull(thumbnailPng);

        SourceKind = sourceKind;
        SemanticHash = semanticHash;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        HasAlpha = hasAlpha;
        DpiXMilli = dpiXMilli;
        DpiYMilli = dpiYMilli;
        ThumbnailPixelWidth = thumbnailPixelWidth;
        ThumbnailPixelHeight = thumbnailPixelHeight;
        _canonicalPng = canonicalPng;
        _thumbnailPng = thumbnailPng;
    }

    public ClipboardRepresentationKind SourceKind { get; }

    public string SemanticHash { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool HasAlpha { get; }

    public int? DpiXMilli { get; }

    public int? DpiYMilli { get; }

    public int ThumbnailPixelWidth { get; }

    public int ThumbnailPixelHeight { get; }

    public long CanonicalPngByteLength =>
        GetLease(_canonicalPng).Length;

    public long ThumbnailPngByteLength =>
        GetLease(_thumbnailPng).Length;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Stream OpenCanonicalPngReadStream() =>
        GetLease(_canonicalPng).OpenRead();

    public Stream OpenThumbnailPngReadStream() =>
        GetLease(_thumbnailPng).OpenRead();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        DisposeOne(Interlocked.Exchange(ref _thumbnailPng, null), ref failure);
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
}
