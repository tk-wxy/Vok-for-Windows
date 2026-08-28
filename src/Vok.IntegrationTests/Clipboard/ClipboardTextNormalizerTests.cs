using System.Buffers.Binary;
using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardTextNormalizerTests
{
    [Fact]
    public void UnicodeTextPreservesObservableWhitespaceAndNewlines()
    {
        var normalizer = new ClipboardTextNormalizer();
        const string text = "  first\r\nsecond  ";

        var content = normalizer.NormalizeText(Capture(
            Format(
                ClipboardRepresentationKind.UnicodeText,
                Utf16(text))));

        Assert.NotNull(content);
        Assert.Equal(text, content.PlainText);
        Assert.Equal(text, content.PlainPreview);
        Assert.Equal(64, content.SemanticHash.Length);
        var representation = Assert.Single(content.Representations);
        Assert.Equal(ClipboardRepresentationKind.UnicodeText, representation.Kind);
    }

    [Fact]
    public void AnsiUsesClipboardLocaleAndProducesUnicodeReplay()
    {
        var normalizer = new ClipboardTextNormalizer();
        var encoding = Encoding.GetEncoding(936);
        var locale = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(locale, 0x0804);

        var content = normalizer.NormalizeText(Capture(
            Format(
                ClipboardRepresentationKind.TextLocale,
                locale,
                ClipboardFormatRole.Metadata),
            Format(
                ClipboardRepresentationKind.AnsiText,
                AppendNull(encoding.GetBytes("中文")))));

        Assert.NotNull(content);
        Assert.Equal("中文", content.PlainText);
        Assert.Equal(
            ClipboardRepresentationKind.UnicodeText,
            Assert.Single(content.Representations).Kind);
    }

    [Fact]
    public void HtmlAndRtfRemainRepresentationsWhileUnicodeDrivesPreview()
    {
        var normalizer = new ClipboardTextNormalizer();
        var html = "Version:1.0\r\n<!--StartFragment--><b>Hello</b><!--EndFragment-->";
        var rtf = "{\\rtf1\\ansi \\b Hello\\b0}";

        var content = normalizer.NormalizeText(Capture(
            Format(ClipboardRepresentationKind.Html, Utf8(html)),
            Format(ClipboardRepresentationKind.Rtf, Latin1(rtf)),
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("Hello"))));

        Assert.NotNull(content);
        Assert.Equal("Hello", content.PlainText);
        Assert.Equal(
            [
                ClipboardRepresentationKind.Html,
                ClipboardRepresentationKind.Rtf,
                ClipboardRepresentationKind.UnicodeText,
            ],
            content.Representations.Select(static value => value.Kind));
    }

    [Fact]
    public void RichFormattingParticipatesInSemanticIdentity()
    {
        var normalizer = new ClipboardTextNormalizer();
        var first = normalizer.NormalizeText(Capture(
            Format(ClipboardRepresentationKind.Html, Utf8("<b>Hello</b>")),
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("Hello"))));
        var second = normalizer.NormalizeText(Capture(
            Format(ClipboardRepresentationKind.Html, Utf8("<i>Hello</i>")),
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("Hello"))));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.SemanticHash, second.SemanticHash);
    }

    [Fact]
    public void HtmlOnlyUsesSafeFragmentTextForUnicodeFallback()
    {
        var normalizer = new ClipboardTextNormalizer();
        var content = normalizer.NormalizeText(Capture(
            Format(
                ClipboardRepresentationKind.Html,
                Utf8("<!--StartFragment--><p>Hello &amp; world</p><!--EndFragment-->"))));

        Assert.NotNull(content);
        Assert.Equal("Hello & world", content.PlainText);
        Assert.Contains(
            content.Representations,
            static value => value.Kind is ClipboardRepresentationKind.UnicodeText);
    }

    [Fact]
    public void RtfOnlyDecodesUnicodeControlWordsWithoutExecutingObjects()
    {
        var normalizer = new ClipboardTextNormalizer();
        var content = normalizer.NormalizeText(Capture(
            Format(
                ClipboardRepresentationKind.Rtf,
                Latin1("{\\rtf1\\ansi Hello \\u20320?{\\object hidden}}"))));

        Assert.NotNull(content);
        Assert.Equal("Hello 你", content.PlainText);
        Assert.DoesNotContain("hidden", content.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedRichRepresentationDoesNotPoisonValidUnicode()
    {
        var normalizer = new ClipboardTextNormalizer();
        var content = normalizer.NormalizeText(Capture(
            Format(ClipboardRepresentationKind.Html, [0xff, 0xfe, 0]),
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("safe"))));

        Assert.NotNull(content);
        Assert.Equal("safe", content.PlainText);
        Assert.DoesNotContain(
            content.Representations,
            static value => value.Kind is ClipboardRepresentationKind.Html);
    }

    [Theory]
    [InlineData(ClipboardRepresentationKind.FileDrop, ClipboardFormatCaptureState.Captured)]
    [InlineData(ClipboardRepresentationKind.Png, ClipboardFormatCaptureState.Captured)]
    [InlineData(ClipboardRepresentationKind.Bitmap, ClipboardFormatCaptureState.MetadataOnly)]
    [InlineData(ClipboardRepresentationKind.Png, ClipboardFormatCaptureState.Oversize)]
    public void StrongerContentPreventsImageOrFileFromBecomingTextCard(
        ClipboardRepresentationKind kind,
        ClipboardFormatCaptureState state)
    {
        var normalizer = new ClipboardTextNormalizer();
        var capture = Capture(
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("fallback")),
            new ClipboardFormatSnapshot(
                1,
                kind.ToString(),
                kind,
                ClipboardFormatRole.Content,
                state,
                state is ClipboardFormatCaptureState.Captured ? new byte[] { 1 } : []));

        Assert.Null(normalizer.NormalizeText(capture));
    }

    [Fact]
    public void NamedCharacterLimitRejectsOversizeText()
    {
        var normalizer = new ClipboardTextNormalizer(
            new ClipboardTextNormalizationOptions(
                maximumTextCharacters: 4,
                maximumRichTextCharacters: 16,
                previewCharacters: 4));

        Assert.Null(normalizer.NormalizeText(Capture(
            Format(ClipboardRepresentationKind.UnicodeText, Utf16("12345")))));
    }

    private static ClipboardCaptureResult Capture(
        params ClipboardFormatSnapshot[] formats) => new(
        RuntimeSequence: 10,
        DateTimeOffset.UtcNow,
        ClipboardCaptureDisposition.Captured,
        formats);

    private static ClipboardFormatSnapshot Format(
        ClipboardRepresentationKind kind,
        byte[] payload,
        ClipboardFormatRole role = ClipboardFormatRole.Content) => new(
        SourceOrder: 0,
        kind.ToString(),
        kind,
        role,
        ClipboardFormatCaptureState.Captured,
        payload);

    private static byte[] Utf16(string text) =>
        AppendNull(Encoding.Unicode.GetBytes(text), terminatorBytes: 2);

    private static byte[] Utf8(string text) =>
        AppendNull(Encoding.UTF8.GetBytes(text));

    private static byte[] Latin1(string text) =>
        AppendNull(Encoding.Latin1.GetBytes(text));

    private static byte[] AppendNull(byte[] bytes, int terminatorBytes = 1)
    {
        var result = new byte[bytes.Length + terminatorBytes];
        bytes.CopyTo(result, 0);
        return result;
    }
}
