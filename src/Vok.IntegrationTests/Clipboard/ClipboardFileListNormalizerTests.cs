using System.Buffers.Binary;
using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardFileListNormalizerTests
{
    [Fact]
    public void UnicodeCopyPreservesOrderAndProducesVersionedIdentity()
    {
        var normalizer = new ClipboardFileListNormalizer();

        var content = normalizer.Normalize(Capture(
            FileDrop(@"C:\Temp\报告.txt", @"C:\Temp\Folder"),
            DropEffect(1)));

        Assert.NotNull(content);
        Assert.Equal(ClipboardFileTransferIntent.Copy, content.Intent);
        Assert.Equal(64, content.SemanticHash.Length);
        Assert.Equal([@"C:\Temp\报告.txt", @"C:\Temp\Folder"],
            content.Files.Select(static file => file.CanonicalPath));
        Assert.Equal([0, 1], content.Files.Select(static file => file.Ordinal));
        Assert.Equal("报告.txt + 另外 1 项", content.PlainPreview);
    }

    [Fact]
    public void ExplorerCopyWithCopyAndLinkBitsNormalizesAsCopy()
    {
        var normalizer = new ClipboardFileListNormalizer();

        var content = normalizer.Normalize(Capture(
            FileDrop(@"C:\Temp\Explorer-Copy.txt"),
            DropEffect(5)));

        Assert.NotNull(content);
        Assert.Equal(ClipboardFileTransferIntent.Copy, content.Intent);
        Assert.Equal(
            @"C:\Temp\Explorer-Copy.txt",
            Assert.Single(content.Files).CanonicalPath);
    }

    [Fact]
    public void MissingEffectIsUnknownAndReorderedPathsChangeIdentity()
    {
        var normalizer = new ClipboardFileListNormalizer();
        var first = normalizer.Normalize(Capture(FileDrop(@"C:\A.txt", @"C:\B.txt")));
        var reordered = normalizer.Normalize(Capture(FileDrop(@"C:\B.txt", @"C:\A.txt")));

        Assert.NotNull(first);
        Assert.NotNull(reordered);
        Assert.Equal(ClipboardFileTransferIntent.Unknown, first.Intent);
        Assert.NotEqual(first.SemanticHash, reordered.SemanticHash);
    }

    [Fact]
    public void ExplicitNoneEffectIsUnknown()
    {
        var content = new ClipboardFileListNormalizer().Normalize(Capture(
            FileDrop(@"C:\A.txt"),
            DropEffect(0)));

        Assert.NotNull(content);
        Assert.Equal(ClipboardFileTransferIntent.Unknown, content.Intent);
    }

    [Fact]
    public void CopyMoveAndUnknownHaveDistinctIdentity()
    {
        var normalizer = new ClipboardFileListNormalizer();
        var copy = normalizer.Normalize(Capture(FileDrop(@"C:\A.txt"), DropEffect(1)));
        var move = normalizer.Normalize(Capture(FileDrop(@"C:\A.txt"), DropEffect(2)));
        var unknown = normalizer.Normalize(Capture(FileDrop(@"C:\A.txt")));

        Assert.NotNull(copy);
        Assert.NotNull(move);
        Assert.NotNull(unknown);
        Assert.NotEqual(copy.SemanticHash, move.SemanticHash);
        Assert.NotEqual(copy.SemanticHash, unknown.SemanticHash);
    }

    [Fact]
    public void WindowsPathCasingDoesNotChangeIdentity()
    {
        var normalizer = new ClipboardFileListNormalizer();
        var upper = normalizer.Normalize(Capture(FileDrop(@"C:\Temp\A.TXT"), DropEffect(1)));
        var lower = normalizer.Normalize(Capture(FileDrop(@"c:\temp\a.txt"), DropEffect(1)));

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.Equal(upper.SemanticHash, lower.SemanticHash);
    }

    [Fact]
    public void AnsiDropFilesUsesSystemCodePageForAbsolutePaths()
    {
        var normalizer = new ClipboardFileListNormalizer();
        var content = normalizer.Normalize(Capture(new ClipboardFormatSnapshot(
            0,
            "CF_HDROP",
            ClipboardRepresentationKind.FileDrop,
            ClipboardFormatRole.Content,
            ClipboardFormatCaptureState.Captured,
            CreateFileDropPayload([@"C:\Temp\ansi.txt"], isWide: false))));

        Assert.NotNull(content);
        Assert.Equal(@"C:\Temp\ansi.txt", Assert.Single(content.Files).CanonicalPath);
    }

    [Theory]
    [InlineData(1u, ClipboardFileTransferIntent.Copy)]
    [InlineData(5u, ClipboardFileTransferIntent.Copy)]
    [InlineData(0x80000001u, ClipboardFileTransferIntent.Copy)]
    [InlineData(2u, ClipboardFileTransferIntent.Move)]
    [InlineData(6u, ClipboardFileTransferIntent.Move)]
    [InlineData(0x80000002u, ClipboardFileTransferIntent.Move)]
    public void SingleTransferIntentBitWinsOverAdditionalEffectBits(
        uint effect,
        ClipboardFileTransferIntent expectedIntent)
    {
        var normalizer = new ClipboardFileListNormalizer();

        var content = normalizer.Normalize(Capture(
            FileDrop(@"C:\A.txt"),
            DropEffect(effect)));

        Assert.NotNull(content);
        Assert.Equal(expectedIntent, content.Intent);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(7u)]
    [InlineData(0x10u)]
    public void AmbiguousOrUnsupportedDropEffectIsRejected(uint effect)
    {
        var normalizer = new ClipboardFileListNormalizer();

        Assert.Null(normalizer.Normalize(Capture(
            FileDrop(@"C:\A.txt"),
            DropEffect(effect))));
    }

    [Fact]
    public void TruncatedDropEffectRejectsWholeFileSnapshot()
    {
        var truncatedEffect = new ClipboardFormatSnapshot(
            1,
            "Preferred DropEffect",
            ClipboardRepresentationKind.PreferredDropEffect,
            ClipboardFormatRole.Metadata,
            ClipboardFormatCaptureState.Captured,
            new byte[] { 1, 0, 0 });

        Assert.Null(new ClipboardFileListNormalizer().Normalize(Capture(
            FileDrop(@"C:\A.txt"),
            truncatedEffect)));
    }

    [Fact]
    public void MalformedFileDropDoesNotFallBackToTextInRouter()
    {
        var payload = CreateFileDropPayload([@"C:\A.txt"]);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, uint.MaxValue);
        var router = new ClipboardContentNormalizer();

        var content = router.Normalize(Capture(
            new ClipboardFormatSnapshot(
                0,
                "CF_HDROP",
                ClipboardRepresentationKind.FileDrop,
                ClipboardFormatRole.Content,
                ClipboardFormatCaptureState.Captured,
                payload),
            new ClipboardFormatSnapshot(
                1,
                "CF_UNICODETEXT",
                ClipboardRepresentationKind.UnicodeText,
                ClipboardFormatRole.Content,
                ClipboardFormatCaptureState.Captured,
                Encoding.Unicode.GetBytes("must-not-become-text\0"))));

        Assert.Null(content);
    }

    [Fact]
    public void FileDropWinsOverTextAndImageFallbacks()
    {
        var router = new ClipboardContentNormalizer();

        var content = router.Normalize(Capture(
            new ClipboardFormatSnapshot(
                0,
                "CF_UNICODETEXT",
                ClipboardRepresentationKind.UnicodeText,
                ClipboardFormatRole.Content,
                ClipboardFormatCaptureState.Captured,
                Encoding.Unicode.GetBytes("fallback\0")),
            FileDrop(@"C:\A.png"),
            new ClipboardFormatSnapshot(
                2,
                "PNG",
                ClipboardRepresentationKind.Png,
                ClipboardFormatRole.Content,
                ClipboardFormatCaptureState.Captured,
                new byte[] { 1, 2, 3 })));

        var files = Assert.IsType<ClipboardFileListContent>(content);
        Assert.Equal("A.png", Assert.Single(files.Files).DisplayName);
    }

    [Fact]
    public void MissingDoubleNullRelativePathAndNamedLimitRejectWholeList()
    {
        var normalizer = new ClipboardFileListNormalizer();
        var unterminated = CreateFileDropPayload([@"C:\A.txt"])[..^2];
        var limited = new ClipboardFileListNormalizer(
            new ClipboardFileNormalizationOptions(
                maximumFileCount: 1,
                maximumPathCharacters: 64,
                maximumAggregatePathCharacters: 64));

        Assert.Null(normalizer.Normalize(Capture(FileDropPayload(unterminated))));
        Assert.Null(normalizer.Normalize(Capture(FileDrop("relative.txt"))));
        Assert.Null(limited.Normalize(Capture(FileDrop(@"C:\A.txt", @"C:\B.txt"))));
    }

    [Fact]
    public void RootsUncExtendedPathsAndDuplicatesRemainOrdered()
    {
        var normalizer = new ClipboardFileListNormalizer();

        var content = normalizer.Normalize(Capture(FileDrop(
            @"C:\",
            @"\\server\share\",
            @"\\?\C:\Long\报告.txt",
            @"C:\")));

        Assert.NotNull(content);
        Assert.Equal(
            [@"C:\", @"\\server\share\", @"\\?\C:\Long\报告.txt", @"C:\"],
            content.Files.Select(static file => file.CanonicalPath));
        Assert.Equal([0, 1, 2, 3], content.Files.Select(static file => file.Ordinal));
    }

    [Fact]
    public void MisalignedWideOffsetInvalidUtf16AndAggregateLimitRejectWholeList()
    {
        var misaligned = CreateFileDropPayload([@"C:\A.txt"]);
        BinaryPrimitives.WriteUInt32LittleEndian(misaligned, 21);
        var invalidUtf16 = CreateFileDropPayload([@"C:\A.txt"]);
        invalidUtf16[20] = 0x00;
        invalidUtf16[21] = 0xD8;
        var aggregateLimited = new ClipboardFileListNormalizer(
            new ClipboardFileNormalizationOptions(
                maximumFileCount: 4,
                maximumPathCharacters: 20,
                maximumAggregatePathCharacters: 20));

        Assert.Null(new ClipboardFileListNormalizer().Normalize(
            Capture(FileDropPayload(misaligned))));
        Assert.Null(new ClipboardFileListNormalizer().Normalize(
            Capture(FileDropPayload(invalidUtf16))));
        Assert.Null(aggregateLimited.Normalize(Capture(FileDrop(
            @"C:\1234567890",
            @"C:\abcdefghij"))));
    }

    [Fact]
    public void EncodedWriterPayloadRoundTripsThroughStrictDecoder()
    {
        var files = new[]
        {
            new ClipboardFileReference(0, @"C:\A.txt", @"C:\A.txt", "A.txt"),
            new ClipboardFileReference(1, @"C:\Folder", @"C:\Folder", "Folder"),
        };

        var payload = ClipboardFileDropCodec.Encode(files);
        var decoded = ClipboardFileDropCodec.TryDecode(
            payload,
            new ClipboardFileNormalizationOptions(),
            out var result);

        Assert.True(decoded);
        Assert.Equal(files.Select(static file => file.CanonicalPath),
            result.Select(static file => file.CanonicalPath));
    }

    private static ClipboardCaptureResult Capture(
        params ClipboardFormatSnapshot[] formats) => new(
        RuntimeSequence: 10,
        DateTimeOffset.UtcNow,
        ClipboardCaptureDisposition.Captured,
        formats);

    private static ClipboardFormatSnapshot FileDrop(params string[] paths) =>
        FileDropPayload(CreateFileDropPayload(paths));

    private static ClipboardFormatSnapshot FileDropPayload(byte[] payload) => new(
        0,
        "CF_HDROP",
        ClipboardRepresentationKind.FileDrop,
        ClipboardFormatRole.Content,
        ClipboardFormatCaptureState.Captured,
        payload);

    private static ClipboardFormatSnapshot DropEffect(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return new ClipboardFormatSnapshot(
            1,
            "Preferred DropEffect",
            ClipboardRepresentationKind.PreferredDropEffect,
            ClipboardFormatRole.Metadata,
            ClipboardFormatCaptureState.Captured,
            bytes);
    }

    private static byte[] CreateFileDropPayload(
        IReadOnlyList<string> paths,
        bool isWide = true)
    {
        const int headerSize = 20;
        var terminatorBytes = isWide ? 2 : 1;
        var encoded = paths.Select(path => isWide
                ? Encoding.Unicode.GetBytes(path)
                : Encoding.ASCII.GetBytes(path))
            .ToArray();
        var result = new byte[checked(
            headerSize + encoded.Sum(static value => value.Length) +
            terminatorBytes * (paths.Count + 1))];
        BinaryPrimitives.WriteUInt32LittleEndian(result, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), isWide ? 1 : 0);
        var offset = headerSize;
        foreach (var path in encoded)
        {
            path.CopyTo(result, offset);
            offset += path.Length + terminatorBytes;
        }

        return result;
    }
}
