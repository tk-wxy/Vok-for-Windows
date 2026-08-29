using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardSnapshotReaderTests
{
    private static readonly Guid InstanceId =
        new("82D9DFE1-6554-4D80-A665-D76E4AD58E8F");

    [Fact]
    public void SupportedFormatsAreCopiedInSourceOrderDuringOneOpenClipboard()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 42 };
        var reader = CreateReader(native);
        var unknown = native.RegisterFormat("Example.Private.Format");
        native.SetFormats(
            (reader.KnownFormats.Html.Id, Encoding.UTF8.GetBytes("<b>hello</b>\0")),
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("hello\0")),
            (unknown, [7, 8, 9]));

        var result = reader.Read((nint)71);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Equal(42u, result.RuntimeSequence);
        Assert.True(result.HasCapturedContent);
        Assert.Equal(1, native.OpenCount);
        Assert.Equal(1, native.CloseCount);
        Assert.Equal((nint)71, native.LastOwnerWindow);
        Assert.Collection(
            result.Formats,
            format =>
            {
                Assert.Equal(ClipboardRepresentationKind.Html, format.RepresentationKind);
                Assert.Equal(ClipboardFormatCaptureState.Captured, format.CaptureState);
                Assert.Equal("<b>hello</b>\0", Encoding.UTF8.GetString(format.Payload.Span));
            },
            format =>
            {
                Assert.Equal(ClipboardRepresentationKind.UnicodeText, format.RepresentationKind);
                Assert.Equal(ClipboardFormatCaptureState.Captured, format.CaptureState);
                Assert.Equal("hello\0", Encoding.Unicode.GetString(format.Payload.Span));
            },
            format =>
            {
                Assert.Equal("Example.Private.Format", format.FormatName);
                Assert.Equal(ClipboardFormatRole.Unknown, format.Role);
                Assert.Equal(ClipboardFormatCaptureState.NotRequested, format.CaptureState);
                Assert.True(format.Payload.IsEmpty);
            });
        Assert.All(native.DataReads, format => Assert.DoesNotContain(format, native.ReadOutsideOpen));
    }

    [Fact]
    public void ExcludeMarkerSkipsAllClipboardContentWithoutReadingIt()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 50 };
        var reader = CreateReader(native);
        native.SetFormats(
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("secret\0")),
            (reader.KnownFormats.ExcludeFromMonitor.Id, [1]));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Excluded, result.Disposition);
        Assert.Empty(native.DataReads);
        Assert.All(
            result.Formats,
            format => Assert.Equal(
                ClipboardFormatCaptureState.NotRequested,
                format.CaptureState));
    }

    [Fact]
    public void ExplicitHistoryOptOutFailsClosedBeforeContentRead()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 51 };
        var reader = CreateReader(native);
        native.SetFormats(
            (reader.KnownFormats.IncludeInHistory.Id, [0, 0, 0, 0]),
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("secret\0")));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Excluded, result.Disposition);
        Assert.Equal([reader.KnownFormats.IncludeInHistory.Id], native.DataReads);
    }

    [Fact]
    public void MatchingInternalMarkerSuppressesOnlyThisServiceInstance()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 52 };
        var reader = CreateReader(native);
        native.SetFormats(
            (reader.KnownFormats.InternalWriteMarker.Id, InstanceId.ToByteArray()),
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("self\0")));

        var ownResult = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.SelfWrite, ownResult.Disposition);
        Assert.Equal([reader.KnownFormats.InternalWriteMarker.Id], native.DataReads);

        native.SetFormats(
            (reader.KnownFormats.InternalWriteMarker.Id, Guid.NewGuid().ToByteArray()),
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("external\0")));
        var externalResult = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, externalResult.Disposition);
        Assert.Contains(
            externalResult.Formats,
            format => format.RepresentationKind is ClipboardRepresentationKind.UnicodeText &&
                      format.CaptureState is ClipboardFormatCaptureState.Captured);
    }

    [Fact]
    public void BusyClipboardReturnsWithoutClosingAnUnownedClipboard()
    {
        using var native = new FakeWindowsClipboardNativeApi
        {
            Sequence = 60,
            FailOpen = true,
        };
        var reader = CreateReader(native);

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Busy, result.Disposition);
        Assert.Equal(1, native.OpenCount);
        Assert.Equal(0, native.CloseCount);
        Assert.Empty(native.DataReads);
    }

    [Fact]
    public void OversizeRepresentationDoesNotPreventLowerCostFallbackCapture()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 61 };
        var options = new ClipboardCaptureOptions(
            maximumFormatBytes: 4,
            maximumSnapshotBytes: 6,
            busyRetrySchedule: [TimeSpan.FromMilliseconds(1)]);
        var reader = new WindowsClipboardSnapshotReader(native, options, InstanceId);
        native.SetFormats(
            (reader.KnownFormats.Html.Id, [1, 2, 3, 4, 5]),
            (ClipboardKnownFormats.AnsiText, [65, 0]));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Collection(
            result.Formats,
            format => Assert.Equal(
                ClipboardFormatCaptureState.Oversize,
                format.CaptureState),
            format =>
            {
                Assert.Equal(ClipboardFormatCaptureState.Captured, format.CaptureState);
                Assert.Equal([65, 0], format.Payload.ToArray());
            });
        Assert.Equal(5, result.Formats[0].SourceByteLength);
        Assert.Equal(2, result.Formats[1].SourceByteLength);
    }

    [Fact]
    public void SnapshotByteBudgetAppliesAcrossRepresentations()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 62 };
        var options = new ClipboardCaptureOptions(
            maximumFormatBytes: 4,
            maximumSnapshotBytes: 6,
            busyRetrySchedule: [TimeSpan.FromMilliseconds(1)]);
        var reader = new WindowsClipboardSnapshotReader(native, options, InstanceId);
        native.SetFormats(
            (reader.KnownFormats.Html.Id, [1, 2, 3, 4]),
            (ClipboardKnownFormats.AnsiText, [5, 6, 7, 8]));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Equal(
            ClipboardFormatCaptureState.Captured,
            result.Formats[0].CaptureState);
        Assert.Equal(
            ClipboardFormatCaptureState.Oversize,
            result.Formats[1].CaptureState);
        Assert.Equal(4, result.Formats[0].SourceByteLength);
        Assert.Equal(4, result.Formats[1].SourceByteLength);
    }

    [Fact]
    public void BitmapIsCataloguedWithoutTreatingItsHandleAsGlobalMemory()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 63 };
        var reader = CreateReader(native);
        native.SetFormats((ClipboardKnownFormats.Bitmap, null));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Unsupported, result.Disposition);
        var bitmap = Assert.Single(result.Formats);
        Assert.Equal(ClipboardRepresentationKind.Bitmap, bitmap.RepresentationKind);
        Assert.Equal(ClipboardFormatCaptureState.MetadataOnly, bitmap.CaptureState);
        Assert.Equal([ClipboardKnownFormats.DibV5], native.DataReads);
    }

    [Fact]
    public void ImageDirectoryOutranksFileAndTextAndCopiesOnlyPng()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 632 };
        var reader = CreateReader(native);
        native.SetFormats(
            (ClipboardKnownFormats.FileDrop, [1, 2, 3, 4]),
            (ClipboardKnownFormats.UnicodeText, Encoding.Unicode.GetBytes("fallback\0")),
            (reader.KnownFormats.Png.Id, CreatePng(64)),
            (ClipboardKnownFormats.DibV5, CreateDibV5()),
            (ClipboardKnownFormats.Dib, CreateDib()));

        using var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Equal([reader.KnownFormats.Png.Id], native.DataReads);
        var png = Assert.Single(result.Formats, static format =>
            format.CaptureState is ClipboardFormatCaptureState.Captured);
        Assert.Equal(ClipboardRepresentationKind.Png, png.RepresentationKind);
        Assert.Equal(ClipboardPayloadStorageKind.Inline, png.PayloadStorageKind);
        Assert.Equal(2, png.ImageHeader?.PixelWidth);
        Assert.All(
            result.Formats.Where(static format => format.RepresentationKind is
                ClipboardRepresentationKind.FileDrop or
                ClipboardRepresentationKind.UnicodeText or
                ClipboardRepresentationKind.DibV5 or
                ClipboardRepresentationKind.Dib),
            static format => Assert.Equal(
                ClipboardFormatCaptureState.NotRequested,
                format.CaptureState));
        Assert.Null(new ClipboardContentNormalizer().Normalize(result));
    }

    [Fact]
    public void InvalidPngFallsBackToOneDibV5CandidateWithoutReadingDib()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 633 };
        var reader = CreateReader(native);
        native.SetFormats(
            (reader.KnownFormats.Png.Id, new byte[64]),
            (ClipboardKnownFormats.DibV5, CreateDibV5()),
            (ClipboardKnownFormats.Dib, CreateDib()));

        using var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Equal(
            [reader.KnownFormats.Png.Id, ClipboardKnownFormats.DibV5],
            native.DataReads);
        Assert.Equal(ClipboardFormatCaptureState.Failed, result.Formats[0].CaptureState);
        Assert.Equal(ClipboardFormatCaptureState.Captured, result.Formats[1].CaptureState);
        Assert.Equal(ClipboardFormatCaptureState.NotRequested, result.Formats[2].CaptureState);
    }

    [Fact]
    public void LargeImageUsesStagingLeaseAndDisposeReleasesFileAndReservation()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "Vok.ClipboardSnapshotReaderTests",
            Guid.NewGuid().ToString("N"));
        var imageOptions = CreateImageOptions(inlinePayloadBytes: 32);
        var options = new ClipboardCaptureOptions(image: imageOptions);
        using var store = new ClipboardStagingStore(stagingPath);
        using var budget = new ClipboardImageReservationBudget(imageOptions);
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 634 };
        var reader = new WindowsClipboardSnapshotReader(
            native,
            options,
            InstanceId,
            stagingStore: store,
            imageReservations: budget);
        var source = CreatePng(256);
        native.SetFormats((reader.KnownFormats.Png.Id, source));

        var result = reader.Read(0);
        var png = Assert.Single(result.Formats);

        Assert.Equal(ClipboardPayloadStorageKind.LeasedBlob, png.PayloadStorageKind);
        Assert.True(png.Payload.IsEmpty);
        Assert.Equal(source.Length, png.PayloadByteLength);
        Assert.Equal(1, store.OutstandingLeaseCount);
        Assert.Equal(1, budget.CurrentJobCount);
        Assert.Equal(source.Length + 16, budget.CurrentReservedBytes);
        using (var stream = png.OpenPayloadReadStream())
        {
            Assert.Equal(source, ReadAll(stream));
        }

        result.Dispose();
        result.Dispose();

        Assert.Equal(0, store.OutstandingLeaseCount);
        Assert.Equal(0, budget.CurrentJobCount);
        Assert.Equal(0, budget.CurrentReservedBytes);
        Assert.Empty(Directory.EnumerateFiles(stagingPath));
    }

    [Fact]
    public void ReservationCapacityRejectsNextImageUntilFirstResultIsDisposed()
    {
        var imageOptions = CreateImageOptions(
            inlinePayloadBytes: 256,
            imageWorkerCapacity: 1);
        var options = new ClipboardCaptureOptions(image: imageOptions);
        using var budget = new ClipboardImageReservationBudget(imageOptions);
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 635 };
        var reader = new WindowsClipboardSnapshotReader(
            native,
            options,
            InstanceId,
            imageReservations: budget);
        native.SetFormats((reader.KnownFormats.Png.Id, CreatePng(64)));

        var first = reader.Read(0);
        using var rejected = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, first.Disposition);
        Assert.Equal(ClipboardCaptureDisposition.Unsupported, rejected.Disposition);
        Assert.Equal(
            ClipboardFormatCaptureState.CapacityExceeded,
            Assert.Single(rejected.Formats).CaptureState);
        Assert.Equal(1, budget.CurrentJobCount);

        first.Dispose();
        using var acceptedAfterRelease = reader.Read(0);

        Assert.Equal(
            ClipboardCaptureDisposition.Captured,
            acceptedAfterRelease.Disposition);
        Assert.Equal(1, budget.CurrentJobCount);
    }

    [Fact]
    public void CloseClipboardFailureReleasesStagedPayloadBeforeReturningFailure()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "Vok.ClipboardSnapshotReaderTests",
            Guid.NewGuid().ToString("N"));
        var imageOptions = CreateImageOptions(inlinePayloadBytes: 32);
        var options = new ClipboardCaptureOptions(image: imageOptions);
        using var store = new ClipboardStagingStore(stagingPath);
        using var budget = new ClipboardImageReservationBudget(imageOptions);
        using var native = new FakeWindowsClipboardNativeApi
        {
            Sequence = 636,
            FailClose = true,
        };
        var reader = new WindowsClipboardSnapshotReader(
            native,
            options,
            InstanceId,
            stagingStore: store,
            imageReservations: budget);
        native.SetFormats((reader.KnownFormats.Png.Id, CreatePng(256)));

        using var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Failed, result.Disposition);
        Assert.Contains("CloseClipboard", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, store.OutstandingLeaseCount);
        Assert.Equal(0, budget.CurrentJobCount);
        Assert.Empty(Directory.EnumerateFiles(stagingPath));
    }

    [Fact]
    public void ReservationBudgetEnforcesAggregateBytesAndReleasesIdempotently()
    {
        var options = new ClipboardImageCaptureOptions(
            maximumInputBytes: 64,
            maximumDimensionPixels: 4,
            maximumPixelCount: 16,
            maximumCanonicalBytes: 64,
            inlinePayloadBytes: 16,
            leaseCopyBufferBytes: 8,
            imageWorkerCapacity: 4,
            maximumReservedBytes: 100,
            thumbnailMaximumEdgePixels: 4,
            thumbnailCacheBytes: 64);
        using var budget = new ClipboardImageReservationBudget(options);

        Assert.True(budget.TryReserve(60, out var first));
        Assert.False(budget.TryReserve(41, out var rejected));
        Assert.Null(rejected);
        Assert.True(budget.TryReserve(40, out var second));
        Assert.Equal(2, budget.CurrentJobCount);
        Assert.Equal(100, budget.CurrentReservedBytes);

        first!.Dispose();
        first.Dispose();
        second!.Dispose();

        Assert.Equal(0, budget.CurrentJobCount);
        Assert.Equal(0, budget.CurrentReservedBytes);
    }

    [Fact]
    public void StagingStartupDeletesOnlyOwnedStalePayloadPattern()
    {
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "Vok.ClipboardSnapshotReaderTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        var stale = Path.Combine(stagingPath, "capture-stale.blob");
        var unrelated = Path.Combine(stagingPath, "keep.txt");
        File.WriteAllBytes(stale, [1]);
        File.WriteAllBytes(unrelated, [2]);
        try
        {
            using var store = new ClipboardStagingStore(stagingPath);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(unrelated));
            Assert.Equal(0, store.OutstandingLeaseCount);
        }
        finally
        {
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    [Fact]
    public void VirtualFileFormatsAreCataloguedWithoutRequestingArbitraryStreams()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 631 };
        var reader = CreateReader(native);
        native.SetFormats(
            (reader.KnownFormats.FileGroupDescriptorW.Id, null),
            (reader.KnownFormats.FileContents.Id, null));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Unsupported, result.Disposition);
        Assert.All(
            result.Formats,
            static format =>
            {
                Assert.Equal(ClipboardFormatRole.VirtualFile, format.Role);
                Assert.Equal(
                    ClipboardFormatCaptureState.MetadataOnly,
                    format.CaptureState);
            });
        Assert.Empty(native.DataReads);
        Assert.Null(new ClipboardContentNormalizer().Normalize(result));
    }

    [Fact]
    public void EnumerationFailureClosesClipboardAndReturnsFailedSnapshot()
    {
        using var native = new FakeWindowsClipboardNativeApi
        {
            Sequence = 64,
            EnumerationError = 87,
        };
        var reader = CreateReader(native);

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Failed, result.Disposition);
        Assert.Contains("EnumClipboardFormats", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, native.CloseCount);
    }

    private static WindowsClipboardSnapshotReader CreateReader(
        FakeWindowsClipboardNativeApi native) =>
        new(native, new ClipboardCaptureOptions(), InstanceId);

    private static ClipboardImageCaptureOptions CreateImageOptions(
        int inlinePayloadBytes,
        int imageWorkerCapacity = 4) => new(
        maximumInputBytes: 4096,
        maximumDimensionPixels: 64,
        maximumPixelCount: 4096,
        maximumCanonicalBytes: 16 * 1024,
        inlinePayloadBytes: inlinePayloadBytes,
        leaseCopyBufferBytes: 16,
        imageWorkerCapacity: imageWorkerCapacity,
        maximumReservedBytes: 32 * 1024,
        thumbnailMaximumEdgePixels: 32,
        thumbnailCacheBytes: 1024);

    private static byte[] CreatePng(int byteLength)
    {
        var bytes = new byte[Math.Max(byteLength, 29)];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
            .CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 2);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static byte[] CreateDibV5()
    {
        var bytes = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 124);
        WriteDibDimensions(bytes);
        return bytes;
    }

    private static byte[] CreateDib()
    {
        var bytes = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 40);
        WriteDibDimensions(bytes);
        return bytes;
    }

    private static void WriteDibDimensions(Span<byte> bytes)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[14..], 32);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed class FakeWindowsClipboardNativeApi : IWindowsClipboardNativeApi, IDisposable
    {
        private readonly Dictionary<string, uint> _registered =
            new(StringComparer.Ordinal);
        private readonly List<uint> _formats = [];
        private readonly Dictionary<uint, nint> _formatHandles = [];
        private readonly Dictionary<nint, byte[]> _payloads = [];
        private readonly Dictionary<nint, GCHandle> _pins = [];
        private uint _nextRegistered = 0xC000;
        private int _lastError;
        private bool _isOpen;

        public uint Sequence { get; init; }

        public bool FailOpen { get; init; }

        public bool FailClose { get; init; }

        public int EnumerationError { get; init; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public nint LastOwnerWindow { get; private set; }

        public List<uint> DataReads { get; } = [];

        public List<uint> ReadOutsideOpen { get; } = [];

        public uint GetSequenceNumber() => Sequence;

        public uint RegisterFormat(string name)
        {
            if (_registered.TryGetValue(name, out var format))
            {
                return format;
            }

            format = _nextRegistered++;
            _registered.Add(name, format);
            return format;
        }

        public bool Open(nint ownerWindow)
        {
            OpenCount++;
            LastOwnerWindow = ownerWindow;
            if (FailOpen)
            {
                _lastError = 5;
                return false;
            }

            Assert.False(_isOpen);
            _isOpen = true;
            return true;
        }

        public bool Close()
        {
            CloseCount++;
            Assert.True(_isOpen);
            _isOpen = false;
            if (FailClose)
            {
                _lastError = 5;
                return false;
            }

            return true;
        }

        public uint EnumerateFormats(uint previousFormat)
        {
            Assert.True(_isOpen);
            if (EnumerationError != 0)
            {
                _lastError = EnumerationError;
                return 0;
            }

            if (_formats.Count == 0)
            {
                return 0;
            }

            if (previousFormat == 0)
            {
                return _formats[0];
            }

            var index = _formats.IndexOf(previousFormat);
            return index >= 0 && index + 1 < _formats.Count
                ? _formats[index + 1]
                : 0;
        }

        public string? GetFormatName(uint format) =>
            _registered.FirstOrDefault(entry => entry.Value == format).Key;

        public nint GetData(uint format)
        {
            DataReads.Add(format);
            if (!_isOpen)
            {
                ReadOutsideOpen.Add(format);
            }

            return _formatHandles.GetValueOrDefault(format);
        }

        public nuint GetGlobalSize(nint handle) =>
            checked((nuint)_payloads[handle].Length);

        public nint LockGlobal(nint handle)
        {
            var pin = GCHandle.Alloc(_payloads[handle], GCHandleType.Pinned);
            _pins.Add(handle, pin);
            return pin.AddrOfPinnedObject();
        }

        public void UnlockGlobal(nint handle)
        {
            if (_pins.Remove(handle, out var pin))
            {
                pin.Free();
            }
        }

        public void ResetLastError() => _lastError = 0;

        public int GetLastError() => _lastError;

        public void SetFormats(params (uint Format, byte[]? Payload)[] formats)
        {
            DataReads.Clear();
            _formats.Clear();
            _formatHandles.Clear();
            _payloads.Clear();
            var nextHandle = 1;
            foreach (var (format, payload) in formats)
            {
                _formats.Add(format);
                if (payload is null)
                {
                    continue;
                }

                var handle = (nint)nextHandle++;
                _formatHandles.Add(format, handle);
                _payloads.Add(handle, payload);
            }
        }

        public void Dispose()
        {
            foreach (var pin in _pins.Values)
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
            }

            _pins.Clear();
        }
    }
}
