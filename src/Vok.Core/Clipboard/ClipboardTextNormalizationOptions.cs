namespace Vok.Core.Clipboard;

public sealed class ClipboardTextNormalizationOptions
{
    public const int DefaultMaximumTextCharacters = 1_048_576;
    public const int DefaultMaximumRichTextCharacters = 2_097_152;
    public const int DefaultPreviewCharacters = 512;

    public ClipboardTextNormalizationOptions(
        int maximumTextCharacters = DefaultMaximumTextCharacters,
        int maximumRichTextCharacters = DefaultMaximumRichTextCharacters,
        int previewCharacters = DefaultPreviewCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTextCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRichTextCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(previewCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            previewCharacters,
            maximumTextCharacters);

        MaximumTextCharacters = maximumTextCharacters;
        MaximumRichTextCharacters = maximumRichTextCharacters;
        PreviewCharacters = previewCharacters;
    }

    public int MaximumTextCharacters { get; }

    public int MaximumRichTextCharacters { get; }

    public int PreviewCharacters { get; }
}
