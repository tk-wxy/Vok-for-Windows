namespace Vok.Core.Clipboard;

public enum ClipboardRepresentationKind
{
    Unknown = 0,
    UnicodeText = 1,
    AnsiText = 2,
    Html = 3,
    Rtf = 4,
    FileDrop = 5,
    PreferredDropEffect = 6,
    Png = 7,
    DibV5 = 8,
    Dib = 9,
    Bitmap = 10,
    TextLocale = 11,
    OemText = 12,
}

public enum ClipboardFormatRole
{
    Unknown = 0,
    Content = 1,
    Metadata = 2,
    Privacy = 3,
    Internal = 4,
}

public enum ClipboardFormatCaptureState
{
    NotRequested = 0,
    Captured = 1,
    MetadataOnly = 2,
    Oversize = 3,
    Failed = 4,
}
