using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class ClipboardFileDropCodec
{
    private const int DropFilesSize = 20;
    private const int WideFlagOffset = 16;
    private static readonly UnicodeEncoding StrictUtf16 = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly ClipboardFileNormalizationOptions DefaultOptions = new();

    static ClipboardFileDropCodec()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool TryDecode(
        ReadOnlyMemory<byte> payload,
        ClipboardFileNormalizationOptions options,
        out IReadOnlyList<ClipboardFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(options);
        files = [];
        var bytes = payload.Span;
        if (bytes.Length < DropFilesSize)
        {
            return false;
        }

        var fileOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (fileOffsetValue > int.MaxValue)
        {
            return false;
        }

        var fileOffset = (int)fileOffsetValue;
        var isWide = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(WideFlagOffset, sizeof(int))) != 0;
        if (fileOffset < DropFilesSize || fileOffset >= bytes.Length ||
            (isWide && (fileOffset & 1) != 0))
        {
            return false;
        }

        if (!TryDecodePaths(bytes[fileOffset..], isWide, options, out var paths))
        {
            return false;
        }

        var result = new List<ClipboardFileReference>(paths.Count);
        var aggregateCharacters = 0;
        for (var index = 0; index < paths.Count; index++)
        {
            var original = paths[index];
            if (!TryNormalizePath(original, options, out var canonical))
            {
                return false;
            }

            aggregateCharacters = checked(aggregateCharacters + canonical.Length);
            if (aggregateCharacters > options.MaximumAggregatePathCharacters)
            {
                return false;
            }

            var displayName = Path.GetFileName(canonical);
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = canonical;
            }

            result.Add(new ClipboardFileReference(
                index,
                original,
                canonical,
                displayName));
        }

        files = result.AsReadOnly();
        return true;
    }

    internal static bool TryNormalizePath(
        string path,
        ClipboardFileNormalizationOptions options,
        out string canonical)
    {
        ArgumentNullException.ThrowIfNull(options);
        canonical = string.Empty;
        if (string.IsNullOrEmpty(path) ||
            path.Length > options.MaximumPathCharacters ||
            path.Contains('\0', StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(fullPath),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                canonical = Path.EndsInDirectorySeparator(fullPath)
                    ? fullPath
                    : fullPath + Path.DirectorySeparatorChar;
            }
            else
            {
                canonical = Path.TrimEndingDirectorySeparator(fullPath);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return canonical.Length > 0 &&
            canonical.Length <= options.MaximumPathCharacters;
    }

    public static byte[] Encode(IReadOnlyList<ClipboardFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException(
                "A CF_HDROP payload requires at least one path.",
                nameof(files));
        }

        if (files.Count > ClipboardFileNormalizationOptions.DefaultMaximumFileCount)
        {
            throw new ArgumentException(
                "A CF_HDROP payload exceeds the file-count limit.",
                nameof(files));
        }

        var encodedPaths = new List<byte[]>(files.Count);
        var byteCount = checked(DropFilesSize + sizeof(char));
        long aggregateCharacters = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index] ?? throw new ArgumentException(
                "A CF_HDROP payload cannot contain null entries.",
                nameof(files));
            var path = file.CanonicalPath;
            if (file.Ordinal != index || string.IsNullOrEmpty(path) ||
                path.Contains('\0', StringComparison.Ordinal) ||
                !TryNormalizePath(
                    path,
                    DefaultOptions,
                    out var normalized) ||
                !string.Equals(normalized, path, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "CF_HDROP paths must be contiguous, absolute, and free of embedded NUL characters.",
                    nameof(files));
            }


            aggregateCharacters += path.Length;
            if (aggregateCharacters >
                ClipboardFileNormalizationOptions.DefaultMaximumAggregatePathCharacters)
            {
                throw new ArgumentException(
                    "A CF_HDROP payload exceeds the aggregate path limit.",
                    nameof(files));
            }

            var encoded = StrictUtf16.GetBytes(path);
            encodedPaths.Add(encoded);
            byteCount = checked(byteCount + encoded.Length + sizeof(char));
        }

        var result = new byte[byteCount];
        BinaryPrimitives.WriteUInt32LittleEndian(result, DropFilesSize);
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(WideFlagOffset, sizeof(int)),
            1);
        var offset = DropFilesSize;
        foreach (var encoded in encodedPaths)
        {
            encoded.CopyTo(result, offset);
            offset += encoded.Length + sizeof(char);
        }

        return result;
    }

    private static bool TryDecodePaths(
        ReadOnlySpan<byte> payload,
        bool isWide,
        ClipboardFileNormalizationOptions options,
        out List<string> paths)
    {
        paths = [];
        var cursor = 0;
        while (cursor < payload.Length)
        {
            var terminator = isWide
                ? FindWideNull(payload, cursor)
                : payload[cursor..].IndexOf((byte)0) is var relative && relative >= 0
                    ? cursor + relative
                    : -1;
            if (terminator < 0)
            {
                return false;
            }

            if (terminator == cursor)
            {
                return paths.Count != 0;
            }

            string path;
            try
            {
                path = isWide
                    ? StrictUtf16.GetString(payload[cursor..terminator])
                    : Encoding.GetEncoding(
                        CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
                        EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback)
                        .GetString(payload[cursor..terminator]);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }

            paths.Add(path);
            if (paths.Count > options.MaximumFileCount)
            {
                return false;
            }

            cursor = checked(terminator + (isWide ? sizeof(char) : 1));
        }

        return false;
    }

    private static int FindWideNull(ReadOnlySpan<byte> payload, int start)
    {
        for (var index = start; index + 1 < payload.Length; index += sizeof(char))
        {
            if (payload[index] == 0 && payload[index + 1] == 0)
            {
                return index;
            }
        }

        return -1;
    }
}
