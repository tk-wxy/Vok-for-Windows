using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ClipboardTextNormalizer
{
    private static readonly UnicodeEncoding StrictUtf16 = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ClipboardTextNormalizationOptions _options;

    static ClipboardTextNormalizer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ClipboardTextNormalizer()
        : this(new ClipboardTextNormalizationOptions())
    {
    }

    public ClipboardTextNormalizer(ClipboardTextNormalizationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ClipboardTextContent? NormalizeText(ClipboardCaptureResult capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Disposition is not ClipboardCaptureDisposition.Captured ||
            HasStrongerNonTextContent(capture.Formats))
        {
            return null;
        }

        var locale = ReadLocale(capture.Formats);
        var unicode = DecodeFirst(
            capture.Formats,
            ClipboardRepresentationKind.UnicodeText,
            DecodeUnicode);
        var ansi = DecodeFirst(
            capture.Formats,
            ClipboardRepresentationKind.AnsiText,
            payload => DecodeCodePage(payload, ResolveAnsiCodePage(locale)));
        var oem = DecodeFirst(
            capture.Formats,
            ClipboardRepresentationKind.OemText,
            payload => DecodeCodePage(payload, ResolveOemCodePage(locale)));
        var html = DecodeHtml(capture.Formats);
        var rtf = DecodeRtf(capture.Formats);

        var plainText = unicode?.Text ?? ansi?.Text ?? oem?.Text ??
            html?.PlainText ?? rtf?.PlainText;
        if (plainText is null || plainText.Length > _options.MaximumTextCharacters)
        {
            return null;
        }

        var representations = new List<ClipboardStoredRepresentation>(3);
        if (html is not null && html.RawText.Length <= _options.MaximumRichTextCharacters)
        {
            representations.Add(new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Html,
                FidelityRank: 300,
                html.RawText,
                html.ByteLength));
        }

        if (rtf is not null && rtf.RawText.Length <= _options.MaximumRichTextCharacters)
        {
            representations.Add(new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Rtf,
                FidelityRank: 200,
                rtf.RawText,
                rtf.ByteLength));
        }

        representations.Add(new ClipboardStoredRepresentation(
            ClipboardRepresentationKind.UnicodeText,
            FidelityRank: 100,
            plainText,
            unicode?.ByteLength ?? checked(StrictUtf16.GetByteCount(plainText) + 2)));

        var frozen = Array.AsReadOnly(representations.ToArray());
        return new ClipboardTextContent(
            ClipboardRepresentationSemanticHash.Compute(frozen),
            plainText,
            CreatePreview(plainText),
            frozen);
    }

    private static bool HasStrongerNonTextContent(
        IEnumerable<ClipboardFormatSnapshot> formats) => formats.Any(static format =>
            format.Role is ClipboardFormatRole.Content &&
            format.CaptureState is not ClipboardFormatCaptureState.NotRequested &&
            format.RepresentationKind is
                ClipboardRepresentationKind.FileDrop or
                ClipboardRepresentationKind.Png or
                ClipboardRepresentationKind.DibV5 or
                ClipboardRepresentationKind.Dib or
                ClipboardRepresentationKind.Bitmap);

    private static uint? ReadLocale(IEnumerable<ClipboardFormatSnapshot> formats)
    {
        var payload = formats
            .Where(static format =>
                format.RepresentationKind is ClipboardRepresentationKind.TextLocale &&
                format.CaptureState is ClipboardFormatCaptureState.Captured)
            .OrderBy(static format => format.SourceOrder)
            .Select(static format => format.Payload)
            .FirstOrDefault();
        return payload.Length >= sizeof(uint)
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Span)
            : null;
    }

    private static DecodedText? DecodeFirst(
        IEnumerable<ClipboardFormatSnapshot> formats,
        ClipboardRepresentationKind kind,
        Func<ReadOnlyMemory<byte>, string?> decoder)
    {
        foreach (var format in formats
                     .Where(format =>
                         format.RepresentationKind == kind &&
                         format.CaptureState is ClipboardFormatCaptureState.Captured)
                     .OrderBy(static format => format.SourceOrder))
        {
            var text = decoder(format.Payload);
            if (text is not null)
            {
                return new DecodedText(text, format.ByteLength);
            }
        }

        return null;
    }

    private static string? DecodeUnicode(ReadOnlyMemory<byte> payload)
    {
        var length = payload.Length & ~1;
        var span = payload.Span[..length];
        for (var index = 0; index + 1 < span.Length; index += 2)
        {
            if (span[index] == 0 && span[index + 1] == 0)
            {
                span = span[..index];
                break;
            }
        }

        try
        {
            return StrictUtf16.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? DecodeCodePage(
        ReadOnlyMemory<byte> payload,
        int codePage)
    {
        var span = TrimAtNull(payload.Span);
        try
        {
            return Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static HtmlText? DecodeHtml(IEnumerable<ClipboardFormatSnapshot> formats)
    {
        foreach (var format in formats
                     .Where(static format =>
                         format.RepresentationKind is ClipboardRepresentationKind.Html &&
                         format.CaptureState is ClipboardFormatCaptureState.Captured)
                     .OrderBy(static format => format.SourceOrder))
        {
            var bytes = TrimAtNull(format.Payload.Span);
            try
            {
                var raw = StrictUtf8.GetString(bytes);
                var fragment = ExtractHtmlFragment(bytes, raw);
                return new HtmlText(raw, StripHtml(fragment), bytes.Length);
            }
            catch (DecoderFallbackException)
            {
                // Try the next representation from the producer.
            }
        }

        return null;
    }

    private static RtfText? DecodeRtf(IEnumerable<ClipboardFormatSnapshot> formats)
    {
        foreach (var format in formats
                     .Where(static format =>
                         format.RepresentationKind is ClipboardRepresentationKind.Rtf &&
                         format.CaptureState is ClipboardFormatCaptureState.Captured)
                     .OrderBy(static format => format.SourceOrder))
        {
            var bytes = TrimAtNull(format.Payload.Span);
            var raw = Encoding.Latin1.GetString(bytes);
            if (!raw.StartsWith("{\\rtf", StringComparison.Ordinal))
            {
                continue;
            }

            return new RtfText(raw, ExtractRtfPlainText(raw), bytes.Length);
        }

        return null;
    }

    private static string ExtractHtmlFragment(ReadOnlySpan<byte> bytes, string raw)
    {
        if (TryReadHtmlOffset(bytes, "StartFragment:"u8, out var start) &&
            TryReadHtmlOffset(bytes, "EndFragment:"u8, out var end) &&
            start >= 0 && end >= start && end <= bytes.Length)
        {
            return StrictUtf8.GetString(bytes[start..end]);
        }

        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        var markerStart = raw.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (markerStart < 0)
        {
            return raw;
        }

        markerStart += startMarker.Length;
        var markerEnd = raw.IndexOf(endMarker, markerStart, StringComparison.OrdinalIgnoreCase);
        return markerEnd >= markerStart
            ? raw[markerStart..markerEnd]
            : raw[markerStart..];
    }

    private static bool TryReadHtmlOffset(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> label,
        out int offset)
    {
        offset = 0;
        var labelIndex = bytes.IndexOf(label);
        if (labelIndex < 0)
        {
            return false;
        }

        var index = labelIndex + label.Length;
        var foundDigit = false;
        while (index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'9')
        {
            foundDigit = true;
            var digit = bytes[index++] - (byte)'0';
            if (offset > (int.MaxValue - digit) / 10)
            {
                return false;
            }

            offset = (offset * 10) + digit;
        }

        return foundDigit;
    }

    private static string StripHtml(string html)
    {
        var builder = new StringBuilder(Math.Min(html.Length, 4096));
        var insideTag = false;
        var tag = new StringBuilder(32);
        var suppressedElement = string.Empty;
        for (var index = 0; index < html.Length; index++)
        {
            var character = html[index];
            if (!insideTag && character == '<')
            {
                insideTag = true;
                tag.Clear();
                continue;
            }

            if (insideTag)
            {
                if (character == '>')
                {
                    insideTag = false;
                    var tagText = tag.ToString().Trim();
                    var closing = tagText.StartsWith('/');
                    var nameStart = closing ? 1 : 0;
                    var nameLength = 0;
                    while (nameStart + nameLength < tagText.Length &&
                           char.IsLetterOrDigit(tagText[nameStart + nameLength]))
                    {
                        nameLength++;
                    }

                    var name = nameLength == 0
                        ? string.Empty
                        : tagText.Substring(nameStart, nameLength).ToLowerInvariant();
                    if (!closing && name is "script" or "style")
                    {
                        suppressedElement = name;
                    }
                    else if (closing && name == suppressedElement)
                    {
                        suppressedElement = string.Empty;
                    }
                    else if (suppressedElement.Length == 0 &&
                             name is "br" or "p" or "div" or "li" or "tr")
                    {
                        AppendNewline(builder);
                    }

                    continue;
                }

                if (tag.Length < 256)
                {
                    tag.Append(character);
                }

                continue;
            }

            if (suppressedElement.Length == 0)
            {
                builder.Append(character);
            }
        }

        return WebUtility.HtmlDecode(builder.ToString()).TrimEnd('\r', '\n');
    }

    private static string ExtractRtfPlainText(string rtf)
    {
        var builder = new StringBuilder(Math.Min(rtf.Length, 4096));
        var states = new Stack<RtfGroupState>();
        var state = new RtfGroupState(Skip: false, UnicodeFallbackCount: 1);
        for (var index = 0; index < rtf.Length; index++)
        {
            var character = rtf[index];
            if (character == '{')
            {
                states.Push(state);
                continue;
            }

            if (character == '}')
            {
                if (states.Count != 0)
                {
                    state = states.Pop();
                }

                continue;
            }

            if (character != '\\')
            {
                if (!state.Skip && character is not '\r' and not '\n')
                {
                    builder.Append(character);
                }

                continue;
            }

            if (++index >= rtf.Length)
            {
                break;
            }

            character = rtf[index];
            if (character is '\\' or '{' or '}')
            {
                if (!state.Skip)
                {
                    builder.Append(character);
                }

                continue;
            }

            if (character == '*')
            {
                state = state with { Skip = true };
                continue;
            }

            if (character == '~')
            {
                if (!state.Skip)
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (character == '_' || character == '-')
            {
                if (!state.Skip && character == '_')
                {
                    builder.Append('-');
                }

                continue;
            }

            if (character == '\'')
            {
                if (index + 2 < rtf.Length &&
                    byte.TryParse(
                        rtf.AsSpan(index + 1, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var value) &&
                    !state.Skip)
                {
                    builder.Append(Encoding.GetEncoding(1252).GetString([value]));
                }

                index = Math.Min(index + 2, rtf.Length - 1);
                continue;
            }

            if (!char.IsLetter(character))
            {
                continue;
            }

            var wordStart = index;
            while (index + 1 < rtf.Length && char.IsLetter(rtf[index + 1]))
            {
                index++;
            }

            var word = rtf[wordStart..(index + 1)];
            var sign = 1;
            if (index + 1 < rtf.Length && rtf[index + 1] == '-')
            {
                sign = -1;
                index++;
            }

            var number = 0;
            var hasNumber = false;
            while (index + 1 < rtf.Length && char.IsDigit(rtf[index + 1]))
            {
                hasNumber = true;
                var digit = rtf[++index] - '0';
                number = number > (int.MaxValue - digit) / 10
                    ? int.MaxValue
                    : (number * 10) + digit;
            }

            number *= sign;
            if (index + 1 < rtf.Length && rtf[index + 1] == ' ')
            {
                index++;
            }

            if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or
                "pict" or "object" or "header" or "footer")
            {
                state = state with { Skip = true };
            }
            else if (word == "uc" && hasNumber && number >= 0)
            {
                state = state with { UnicodeFallbackCount = Math.Min(number, 16) };
            }
            else if (word == "u" && hasNumber && !state.Skip)
            {
                builder.Append((char)(number < 0 ? number + 65536 : number));
                index = (int)Math.Min(
                    (long)index + state.UnicodeFallbackCount,
                    rtf.Length - 1);
            }
            else if (!state.Skip && word is "par" or "line")
            {
                AppendNewline(builder);
            }
            else if (!state.Skip && word == "tab")
            {
                builder.Append('\t');
            }
        }

        return builder.ToString();
    }

    private string CreatePreview(string text)
    {
        if (text.Length <= _options.PreviewCharacters)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, _options.PreviewCharacters - 1), "…");
    }

    private static int ResolveAnsiCodePage(uint? locale) =>
        ResolveCodePage(locale, static culture => culture.TextInfo.ANSICodePage, 1252);

    private static int ResolveOemCodePage(uint? locale) =>
        ResolveCodePage(locale, static culture => culture.TextInfo.OEMCodePage, 437);

    private static int ResolveCodePage(
        uint? locale,
        Func<CultureInfo, int> selector,
        int fallback)
    {
        if (locale is null)
        {
            return fallback;
        }

        try
        {
            var codePage = selector(CultureInfo.GetCultureInfo((int)locale.Value));
            return codePage > 0 ? codePage : fallback;
        }
        catch (CultureNotFoundException)
        {
            return fallback;
        }
    }

    private static ReadOnlySpan<byte> TrimAtNull(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        return terminator >= 0 ? bytes[..terminator] : bytes;
    }

    private static void AppendNewline(StringBuilder builder)
    {
        if (builder.Length != 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }
    }

    private sealed record DecodedText(string Text, int ByteLength);

    private sealed record HtmlText(string RawText, string PlainText, int ByteLength);

    private sealed record RtfText(string RawText, string PlainText, int ByteLength);

    private readonly record struct RtfGroupState(bool Skip, int UnicodeFallbackCount);
}
