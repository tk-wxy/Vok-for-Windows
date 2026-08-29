namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed record ClipboardImagePayloadInfo(
    int PixelWidth,
    int PixelHeight,
    int? DpiXMilli,
    int? DpiYMilli,
    long PixelOffset,
    long PixelByteLength,
    bool RequiresColorContext,
    byte[]? BitmapFilePrefix);
