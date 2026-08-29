using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class ClipboardImageNormalizerTests
{
    [Fact]
    public void PngAndBottomUpDib_WithSamePixels_ProduceSameIdentity()
    {
        var pixels = TestPixels.OpaqueTwoByTwo;
        var png = EncodePng(2, 2, pixels, dpi: 96);
        var dib = CreateDib(2, 2, 24, topDown: false, pixels);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var pngResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Png, png, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var dibResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Dib, dib, 2, 2),
            temporary.Store,
            CancellationToken.None);

        Assert.Equal(pngResult.SemanticHash, dibResult.SemanticHash);
        Assert.Equal(
            "4e7b6a2d49565f57be41c538ea532836c408d7ca9e149dd994cbf4d62da28668",
            pngResult.SemanticHash);
        Assert.False(pngResult.HasAlpha);
        Assert.False(dibResult.HasAlpha);
        Assert.Equal(pixels, DecodeBgra32(pngResult.OpenCanonicalPngReadStream()));
        Assert.Equal(pixels, DecodeBgra32(dibResult.OpenCanonicalPngReadStream()));
    }

    [Fact]
    public void TopDownAndBottomUpDib_ProduceSameTopDownPixels()
    {
        var pixels = TestPixels.OpaqueTwoByTwo;
        var topDown = CreateDib(2, 2, 24, topDown: true, pixels);
        var bottomUp = CreateDib(2, 2, 24, topDown: false, pixels);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var topResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Dib, topDown, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var bottomResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Dib, bottomUp, 2, 2),
            temporary.Store,
            CancellationToken.None);

        Assert.Equal(topResult.SemanticHash, bottomResult.SemanticHash);
        Assert.Equal(pixels, DecodeBgra32(topResult.OpenCanonicalPngReadStream()));
        Assert.Equal(pixels, DecodeBgra32(bottomResult.OpenCanonicalPngReadStream()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void SupportedBiRgbDepths_DecodeWithoutEscapingBounds(int bitsPerPixel)
    {
        var pixels = TestPixels.DepthCompatibleTwoByTwo;
        var dib = CreateDib(2, 2, bitsPerPixel, topDown: false, pixels);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var result = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Dib, dib, 2, 2),
            temporary.Store,
            CancellationToken.None);

        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
        Assert.Equal(16, DecodeBgra32(result.OpenCanonicalPngReadStream()).Length);
    }

    [Fact]
    public void DibV5BiRgb_IgnoresStandaloneAlphaMask()
    {
        var pixels = TestPixels.AlphaTwoByTwo;
        var dib = CreateDibV5(
            2,
            2,
            compression: 0,
            alphaMask: 0xff000000,
            pixels);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var result = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.DibV5, dib, 2, 2),
            temporary.Store,
            CancellationToken.None);

        var decoded = DecodeBgra32(result.OpenCanonicalPngReadStream());
        Assert.False(result.HasAlpha);
        Assert.All(
            Enumerable.Range(0, 4),
            index => Assert.Equal(byte.MaxValue, decoded[(index * 4) + 3]));
    }

    [Fact]
    public void DibV5BitFields_PreservesExplicitAlpha()
    {
        var pixels = TestPixels.AlphaTwoByTwo;
        var dib = CreateDibV5(
            2,
            2,
            compression: 3,
            alphaMask: 0xff000000,
            pixels);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var result = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.DibV5, dib, 2, 2),
            temporary.Store,
            CancellationToken.None);

        Assert.True(result.HasAlpha);
        Assert.Equal(pixels, DecodeBgra32(result.OpenCanonicalPngReadStream()));
    }

    [Fact]
    public void DibV5BitFields_AcceptsOnlyExactRedundantWindowsMaskBlock()
    {
        var pixels = TestPixels.AlphaTwoByTwo;
        var compact = CreateDibV5(
            2,
            2,
            compression: 3,
            alphaMask: 0xff000000,
            pixels);
        var repeatedMasks = new byte[12];
        compact.AsSpan(40, 12).CopyTo(repeatedMasks);
        byte[] windowsPacked =
            [.. compact.AsSpan(0, 124), .. repeatedMasks, .. compact.AsSpan(124)];
        byte[] arbitraryTail =
            [.. compact, .. new byte[12]];

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var compactResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.DibV5, compact, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var windowsResult = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.DibV5, windowsPacked, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var invalidCapture = CreateCapture(
            ClipboardRepresentationKind.DibV5,
            arbitraryTail,
            2,
            2);

        Assert.Equal(compactResult.SemanticHash, windowsResult.SemanticHash);
        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(
            invalidCapture,
            temporary.Store,
            CancellationToken.None));
    }

    [Fact]
    public void PngDpi_DoesNotAffectSemanticIdentity()
    {
        var pixels = TestPixels.OpaqueTwoByTwo;
        var png96 = EncodePng(2, 2, pixels, dpi: 96);
        var png144 = EncodePng(2, 2, pixels, dpi: 144);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var first = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Png, png96, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var second = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Png, png144, 2, 2),
            temporary.Store,
            CancellationToken.None);

        Assert.Equal(first.SemanticHash, second.SemanticHash);
        Assert.NotEqual(first.DpiXMilli, second.DpiXMilli);
        Assert.NotEqual(first.DpiYMilli, second.DpiYMilli);
    }

    [Fact]
    public void Thumbnail_IsBoundedAndDoesNotUpscaleCanonical()
    {
        const int width = 900;
        const int height = 300;
        var pixels = Enumerable.Repeat(
                new byte[] { 20, 40, 60, 255 },
                width * height)
            .SelectMany(static value => value)
            .ToArray();
        var png = EncodePng(width, height, pixels, dpi: 96);
        var options = new ClipboardImageCaptureOptions(
            thumbnailMaximumEdgePixels: 128);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(options);
        using var result = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Png, png, width, height),
            temporary.Store,
            CancellationToken.None);

        using var thumbnailStream = result.OpenThumbnailPngReadStream();
        var thumbnail = DecodeFrame(thumbnailStream);
        Assert.Equal(128, thumbnail.PixelWidth);
        Assert.InRange(thumbnail.PixelHeight, 42, 43);
        Assert.Equal(width, result.PixelWidth);
        Assert.Equal(height, result.PixelHeight);
    }

    [Fact]
    public void CanonicalPng_DoesNotCarrySourceTextMetadata()
    {
        var png = EncodePng(2, 2, TestPixels.OpaqueTwoByTwo, dpi: 96);
        var metadataKey = "vok-private-source-key"u8.ToArray();
        var metadataValue = "source-only-value"u8.ToArray();
        var textData = new byte[metadataKey.Length + 1 + metadataValue.Length];
        metadataKey.CopyTo(textData, 0);
        metadataValue.CopyTo(textData, metadataKey.Length + 1);
        var withMetadata = InsertPngChunkBeforeIdat(png, "tEXt"u8, textData);

        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());
        using var result = normalizer.Normalize(
            CreateCapture(ClipboardRepresentationKind.Png, withMetadata, 2, 2),
            temporary.Store,
            CancellationToken.None);
        using var stream = result.OpenCanonicalPngReadStream();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        var canonical = output.ToArray();

        Assert.Equal(-1, canonical.AsSpan().IndexOf(metadataKey));
        Assert.False(ContainsPngChunk(canonical, "tEXt"u8));
        Assert.False(ContainsPngChunk(canonical, "pHYs"u8));
        Assert.False(ContainsPngChunk(canonical, "iCCP"u8));
    }

    [Fact]
    public void CorruptPngCrc_IsRejectedBeforeCodecAndCreatesNoOutputs()
    {
        var png = EncodePng(2, 2, TestPixels.OpaqueTwoByTwo, dpi: 96);
        png[^5] ^= 0x40;
        using var capture = CreateCapture(
            ClipboardRepresentationKind.Png,
            png,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(
            capture,
            temporary.Store,
            CancellationToken.None));
        Assert.Equal(0, temporary.Store.OutstandingLeaseCount);
    }

    [Fact]
    public void AnimatedPngChunk_IsRejectedWithoutStartingDecode()
    {
        var png = EncodePng(2, 2, TestPixels.OpaqueTwoByTwo, dpi: 96);
        var animated = InsertPngChunkBeforeIdat(
            png,
            "acTL"u8,
            [0, 0, 0, 1, 0, 0, 0, 0]);
        using var capture = CreateCapture(
            ClipboardRepresentationKind.Png,
            animated,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<NotSupportedException>(() => normalizer.Normalize(
            capture,
            temporary.Store,
            CancellationToken.None));
    }

    [Fact]
    public void LinkedDibProfile_IsRejectedWithoutOpeningItsPath()
    {
        var dib = CreateDibV5(
            2,
            2,
            compression: 3,
            alphaMask: 0xff000000,
            TestPixels.AlphaTwoByTwo);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), 0x4c494e4b);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(112), (uint)dib.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(116), 8);
        using var capture = CreateCapture(
            ClipboardRepresentationKind.DibV5,
            dib,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<NotSupportedException>(() => normalizer.Normalize(
            capture,
            temporary.Store,
            CancellationToken.None));
    }

    [Fact]
    public void EmbeddedDibProfile_MustBeCompleteAndCodecValid()
    {
        var baseDib = CreateDibV5(
            2,
            2,
            compression: 3,
            alphaMask: 0xff000000,
            TestPixels.AlphaTwoByTwo);
        var invalidProfile = new byte[128];
        var embedded = new byte[baseDib.Length + invalidProfile.Length];
        baseDib.CopyTo(embedded, 0);
        invalidProfile.CopyTo(embedded, baseDib.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(embedded.AsSpan(56), 0x4d424544);
        BinaryPrimitives.WriteUInt32LittleEndian(
            embedded.AsSpan(112),
            checked((uint)baseDib.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            embedded.AsSpan(116),
            checked((uint)invalidProfile.Length));
        using var capture = CreateCapture(
            ClipboardRepresentationKind.DibV5,
            embedded,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(
            capture,
            temporary.Store,
            CancellationToken.None));
        Assert.Equal(0, temporary.Store.OutstandingLeaseCount);
    }

    [Fact]
    public void RleAndEmbeddedCompressedDibs_AreExplicitlyUnsupported()
    {
        var rle = CreateDib(2, 2, 8, topDown: false, TestPixels.DepthCompatibleTwoByTwo);
        BinaryPrimitives.WriteUInt32LittleEndian(rle.AsSpan(16), 1);
        var embeddedPng = CreateDib(2, 2, 24, topDown: false, TestPixels.OpaqueTwoByTwo);
        BinaryPrimitives.WriteUInt32LittleEndian(embeddedPng.AsSpan(16), 5);
        using var firstCapture = CreateCapture(
            ClipboardRepresentationKind.Dib,
            rle,
            2,
            2);
        using var secondCapture = CreateCapture(
            ClipboardRepresentationKind.Dib,
            embeddedPng,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<NotSupportedException>(() => normalizer.Normalize(
            firstCapture,
            temporary.Store,
            CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => normalizer.Normalize(
            secondCapture,
            temporary.Store,
            CancellationToken.None));
    }

    [Fact]
    public void TemporaryStore_StartupCleansOnlyItsOwnedPngPatterns()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "Vok.ImageNormalizerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var normalized = Path.Combine(root, "normalized-stale.png");
        var thumbnail = Path.Combine(root, "thumbnail-stale.png");
        var encoding = Path.Combine(root, "encoding-stale.tmp");
        var capture = Path.Combine(root, "capture-keep.blob");
        var unrelated = Path.Combine(root, "keep.png");
        File.WriteAllBytes(normalized, [1]);
        File.WriteAllBytes(thumbnail, [2]);
        File.WriteAllBytes(encoding, [5]);
        File.WriteAllBytes(capture, [3]);
        File.WriteAllBytes(unrelated, [4]);

        using (var store = new ClipboardImageTemporaryStore(root))
        {
            Assert.False(File.Exists(normalized));
            Assert.False(File.Exists(thumbnail));
            Assert.False(File.Exists(encoding));
            Assert.True(File.Exists(capture));
            Assert.True(File.Exists(unrelated));
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void OverlappingMasksAndTruncatedPixels_AreRejected()
    {
        var overlapping = CreateDibV5(
            2,
            2,
            compression: 3,
            alphaMask: 0xff000000,
            TestPixels.AlphaTwoByTwo);
        BinaryPrimitives.WriteUInt32LittleEndian(overlapping.AsSpan(44), 0x00ff0000);
        var truncated = CreateDib(2, 2, 24, topDown: false, TestPixels.OpaqueTwoByTwo)[..^1];
        using var firstCapture = CreateCapture(
            ClipboardRepresentationKind.DibV5,
            overlapping,
            2,
            2);
        using var secondCapture = CreateCapture(
            ClipboardRepresentationKind.Dib,
            truncated,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(
            firstCapture,
            temporary.Store,
            CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => normalizer.Normalize(
            secondCapture,
            temporary.Store,
            CancellationToken.None));
    }

    [Fact]
    public void SuccessfulResult_OwnsOnlyTemporaryFiles()
    {
        var png = EncodePng(2, 2, TestPixels.OpaqueTwoByTwo, dpi: 96);
        var capture = CreateCapture(
            ClipboardRepresentationKind.Png,
            png,
            2,
            2);
        using var temporary = new TestTemporaryStore();
        var normalizer = new ClipboardImageNormalizer(new ClipboardImageCaptureOptions());

        var result = normalizer.Normalize(
            capture,
            temporary.Store,
            CancellationToken.None);

        Assert.Equal(2, temporary.Store.OutstandingLeaseCount);
        result.Dispose();
        result.Dispose();
        Assert.False(capture.IsDisposed);
        Assert.Equal(0, temporary.Store.OutstandingLeaseCount);
        Assert.True(result.IsDisposed);
        capture.Dispose();
        Assert.True(capture.IsDisposed);
    }

    private static ClipboardCaptureResult CreateCapture(
        ClipboardRepresentationKind kind,
        byte[] payload,
        int width,
        int height)
    {
        var name = kind switch
        {
            ClipboardRepresentationKind.Png => "PNG",
            ClipboardRepresentationKind.DibV5 => "CF_DIBV5",
            _ => "CF_DIB",
        };
        return new ClipboardCaptureResult(
            RuntimeSequence: 1,
            CapturedUtc: DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Captured,
            [
                new ClipboardFormatSnapshot(
                    SourceOrder: 0,
                    name,
                    kind,
                    ClipboardFormatRole.Content,
                    ClipboardFormatCaptureState.Captured,
                    payload,
                    SourceByteLength: payload.Length,
                    ImageHeader: new ClipboardImageHeader(
                        width,
                        height,
                        checked((long)width * height * 4))),
            ]);
    }

    private static byte[] EncodePng(
        int width,
        int height,
        byte[] bgra,
        double dpi)
    {
        var source = BitmapSource.Create(
            width,
            height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            palette: null,
            bgra,
            checked(width * 4));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateDib(
        int width,
        int height,
        int bitsPerPixel,
        bool topDown,
        byte[] topDownBgra)
    {
        var paletteEntries = bitsPerPixel <= 8 ? 1 << bitsPerPixel : 0;
        var headerBytes = 40;
        var paletteBytes = checked(paletteEntries * 4);
        var stride = checked(((width * bitsPerPixel + 31) / 32) * 4);
        var pixelsBytes = checked(stride * height);
        var payload = new byte[checked(headerBytes + paletteBytes + pixelsBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 40);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(8),
            topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(14),
            checked((ushort)bitsPerPixel));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)pixelsBytes);

        for (var index = 0; index < paletteEntries; index++)
        {
            var offset = headerBytes + (index * 4);
            if (index == 0)
            {
                payload[offset + 2] = 255;
            }
            else if (index == 1)
            {
                payload[offset + 1] = 255;
            }
            else
            {
                payload[offset] = payload[offset + 1] = payload[offset + 2] =
                    checked((byte)(index * 255 / Math.Max(1, paletteEntries - 1)));
            }
        }

        var pixelOffset = headerBytes + paletteBytes;
        for (var storedRow = 0; storedRow < height; storedRow++)
        {
            var sourceRow = topDown ? storedRow : height - 1 - storedRow;
            var destination = payload.AsSpan(pixelOffset + (storedRow * stride), stride);
            WriteDibRow(
                destination,
                topDownBgra.AsSpan(sourceRow * width * 4, width * 4),
                width,
                bitsPerPixel);
        }

        return payload;
    }

    private static byte[] CreateDibV5(
        int width,
        int height,
        uint compression,
        uint alphaMask,
        byte[] topDownBgra)
    {
        const int headerBytes = 124;
        var stride = checked(width * 4);
        var pixelsBytes = checked(stride * height);
        var payload = new byte[headerBytes + pixelsBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), (uint)pixelsBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(52), alphaMask);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(56), 0x73524742);

        for (var storedRow = 0; storedRow < height; storedRow++)
        {
            var sourceRow = height - 1 - storedRow;
            topDownBgra.AsSpan(sourceRow * stride, stride)
                .CopyTo(payload.AsSpan(headerBytes + (storedRow * stride), stride));
        }

        return payload;
    }

    private static void WriteDibRow(
        Span<byte> destination,
        ReadOnlySpan<byte> bgra,
        int width,
        int bitsPerPixel)
    {
        for (var x = 0; x < width; x++)
        {
            var source = bgra.Slice(x * 4, 4);
            switch (bitsPerPixel)
            {
                case 1:
                    destination[0] |= checked((byte)((x & 1) << (7 - x)));
                    break;
                case 4:
                    destination[x / 2] |= checked((byte)((x & 1) << (x % 2 == 0 ? 4 : 0)));
                    break;
                case 8:
                    destination[x] = checked((byte)(x & 1));
                    break;
                case 16:
                    var packed = checked((ushort)(
                        ((source[2] >> 3) << 10) |
                        ((source[1] >> 3) << 5) |
                        (source[0] >> 3)));
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[(x * 2)..], packed);
                    break;
                case 24:
                    source[..3].CopyTo(destination[(x * 3)..]);
                    break;
                case 32:
                    source.CopyTo(destination[(x * 4)..]);
                    break;
            }
        }
    }

    private static byte[] InsertPngChunkBeforeIdat(
        byte[] png,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var idat = FindPngChunk(png, "IDAT"u8);
        var chunk = new byte[checked(12 + data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, checked((uint)data.Length));
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(
            chunk.AsSpan(8 + data.Length),
            ComputePngCrc(chunk.AsSpan(4, 4 + data.Length)));
        return [.. png.AsSpan(0, idat), .. chunk, .. png.AsSpan(idat)];
    }

    private static int FindPngChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)));
            if (png.AsSpan(offset + 4, 4).SequenceEqual(type))
            {
                return offset;
            }

            offset = checked(offset + 12 + length);
        }

        throw new InvalidDataException("PNG chunk was not found.");
    }

    private static bool ContainsPngChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)));
            if (png.AsSpan(offset + 4, 4).SequenceEqual(type))
            {
                return true;
            }

            offset = checked(offset + 12 + length);
        }

        return false;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xedb88320u ^ (crc >> 1)
                    : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] DecodeBgra32(Stream stream)
    {
        using (stream)
        {
            var frame = DecodeFrame(stream);
            var converted = new FormatConvertedBitmap(
                frame,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
            var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
            converted.CopyPixels(pixels, checked(converted.PixelWidth * 4), 0);
            return pixels;
        }
    }

    private static BitmapFrame DecodeFrame(Stream stream)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return decoder.Frames.Single();
    }

    private static class TestPixels
    {
        public static readonly byte[] OpaqueTwoByTwo =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            255, 255, 255, 255,
        ];

        public static readonly byte[] AlphaTwoByTwo =
        [
            0, 0, 255, 32,
            0, 255, 0, 96,
            255, 0, 0, 160,
            255, 255, 255, 255,
        ];

        public static readonly byte[] DepthCompatibleTwoByTwo =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            0, 255, 0, 255,
        ];
    }

    private sealed class TestTemporaryStore : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Vok.ImageNormalizerTests",
            Guid.NewGuid().ToString("N"));

        public TestTemporaryStore()
        {
            Store = new ClipboardImageTemporaryStore(_root);
        }

        public ClipboardImageTemporaryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

}
