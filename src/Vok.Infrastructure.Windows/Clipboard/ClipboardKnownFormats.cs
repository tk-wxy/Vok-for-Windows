using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed record ClipboardKnownFormat(
    uint Id,
    string Name,
    ClipboardRepresentationKind RepresentationKind,
    ClipboardFormatRole Role,
    bool UsesGlobalMemory);

internal sealed class ClipboardKnownFormats
{
    internal const uint AnsiText = 1;
    internal const uint Bitmap = 2;
    internal const uint OemText = 7;
    internal const uint Dib = 8;
    internal const uint UnicodeText = 13;
    internal const uint FileDrop = 15;
    internal const uint TextLocale = 16;
    internal const uint DibV5 = 17;

    internal const string HtmlName = "HTML Format";
    internal const string RtfName = "Rich Text Format";
    internal const string PngName = "PNG";
    internal const string PreferredDropEffectName = "Preferred DropEffect";
    internal const string ExcludeFromMonitorName =
        "ExcludeClipboardContentFromMonitorProcessing";
    internal const string IncludeInHistoryName = "CanIncludeInClipboardHistory";
    internal const string InternalWriteMarkerName = "Vok.Internal.ClipboardWrite";

    private readonly Dictionary<uint, ClipboardKnownFormat> _formats;

    public ClipboardKnownFormats(IWindowsClipboardNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        var formats = new[]
        {
            new ClipboardKnownFormat(
                UnicodeText,
                "CF_UNICODETEXT",
                ClipboardRepresentationKind.UnicodeText,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                AnsiText,
                "CF_TEXT",
                ClipboardRepresentationKind.AnsiText,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                OemText,
                "CF_OEMTEXT",
                ClipboardRepresentationKind.OemText,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                Bitmap,
                "CF_BITMAP",
                ClipboardRepresentationKind.Bitmap,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: false),
            new ClipboardKnownFormat(
                Dib,
                "CF_DIB",
                ClipboardRepresentationKind.Dib,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                DibV5,
                "CF_DIBV5",
                ClipboardRepresentationKind.DibV5,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                FileDrop,
                "CF_HDROP",
                ClipboardRepresentationKind.FileDrop,
                ClipboardFormatRole.Content,
                UsesGlobalMemory: true),
            new ClipboardKnownFormat(
                TextLocale,
                "CF_LOCALE",
                ClipboardRepresentationKind.TextLocale,
                ClipboardFormatRole.Metadata,
                UsesGlobalMemory: true),
            Registered(
                native,
                HtmlName,
                ClipboardRepresentationKind.Html,
                ClipboardFormatRole.Content,
                usesGlobalMemory: true),
            Registered(
                native,
                RtfName,
                ClipboardRepresentationKind.Rtf,
                ClipboardFormatRole.Content,
                usesGlobalMemory: true),
            Registered(
                native,
                PngName,
                ClipboardRepresentationKind.Png,
                ClipboardFormatRole.Content,
                usesGlobalMemory: true),
            Registered(
                native,
                PreferredDropEffectName,
                ClipboardRepresentationKind.PreferredDropEffect,
                ClipboardFormatRole.Metadata,
                usesGlobalMemory: true),
            Registered(
                native,
                ExcludeFromMonitorName,
                ClipboardRepresentationKind.Unknown,
                ClipboardFormatRole.Privacy,
                usesGlobalMemory: false),
            Registered(
                native,
                IncludeInHistoryName,
                ClipboardRepresentationKind.Unknown,
                ClipboardFormatRole.Privacy,
                usesGlobalMemory: true),
            Registered(
                native,
                InternalWriteMarkerName,
                ClipboardRepresentationKind.Unknown,
                ClipboardFormatRole.Internal,
                usesGlobalMemory: true),
        };

        _formats = formats.ToDictionary(static format => format.Id);
        Html = formats.Single(static format => format.Name == HtmlName);
        Rtf = formats.Single(static format => format.Name == RtfName);
        Png = formats.Single(static format => format.Name == PngName);
        PreferredDropEffect = formats.Single(
            static format => format.Name == PreferredDropEffectName);
        ExcludeFromMonitor = formats.Single(
            static format => format.Name == ExcludeFromMonitorName);
        IncludeInHistory = formats.Single(
            static format => format.Name == IncludeInHistoryName);
        InternalWriteMarker = formats.Single(
            static format => format.Name == InternalWriteMarkerName);
    }

    public ClipboardKnownFormat Html { get; }

    public ClipboardKnownFormat Rtf { get; }

    public ClipboardKnownFormat Png { get; }

    public ClipboardKnownFormat PreferredDropEffect { get; }

    public ClipboardKnownFormat ExcludeFromMonitor { get; }

    public ClipboardKnownFormat IncludeInHistory { get; }

    public ClipboardKnownFormat InternalWriteMarker { get; }

    public bool TryGet(uint format, out ClipboardKnownFormat knownFormat) =>
        _formats.TryGetValue(format, out knownFormat!);

    private static ClipboardKnownFormat Registered(
        IWindowsClipboardNativeApi native,
        string name,
        ClipboardRepresentationKind representationKind,
        ClipboardFormatRole role,
        bool usesGlobalMemory) =>
        new(
            native.RegisterFormat(name),
            name,
            representationKind,
            role,
            usesGlobalMemory);
}
