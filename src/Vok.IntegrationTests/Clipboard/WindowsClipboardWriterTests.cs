using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardWriterTests
{
    [Fact]
    public void RichTextWriteTransfersFormatsInFidelityOrderAndCreatesReceiptMarker()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 20);
        var known = new ClipboardKnownFormats(native);
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var writer = new WindowsClipboardWriter(native, known, instanceId);
        var payload = RichPayload();

        var receipt = writer.Write(123, payload, operationId);

        Assert.Equal(21u, receipt.RuntimeSequence);
        Assert.Equal(operationId, receipt.OperationId);
        Assert.Equal(payload.SemanticHash, receipt.SemanticHash);
        Assert.Equal(
            [
                known.Html.Id,
                known.Rtf.Id,
                ClipboardKnownFormats.UnicodeText,
                known.InternalWriteMarker.Id,
            ],
            native.TransferredFormats);
        Assert.Equal("Hello", Encoding.Unicode.GetString(
            native.ReadBytes(ClipboardKnownFormats.UnicodeText)[..^2]));
        Assert.True(ClipboardInternalWriteMarker.TryDecode(
            native.ReadBytes(known.InternalWriteMarker.Id),
            out var marker));
        Assert.True(marker.Matches(instanceId, receipt));
        Assert.True(writer.IsCurrent(123, receipt));
    }

    [Fact]
    public void SequenceOrReceiptMismatchPreventsCurrentVerification()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 40);
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());
        var receipt = writer.Write(0, RichPayload(), Guid.NewGuid());

        Assert.False(writer.IsCurrent(0, receipt with { OperationId = Guid.NewGuid() }));
        native.Sequence++;
        Assert.False(writer.IsCurrent(0, receipt));
    }

    [Fact]
    public void SnapshotReaderSuppressesOnlyMarkerBoundToCurrentReceipt()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 60);
        var known = new ClipboardKnownFormats(native);
        var instanceId = Guid.NewGuid();
        var writer = new WindowsClipboardWriter(native, known, instanceId);
        _ = writer.Write(0, RichPayload(), Guid.NewGuid());
        var reader = new WindowsClipboardSnapshotReader(
            native,
            new ClipboardCaptureOptions(),
            instanceId,
            writer.IsExpectedSelfWrite);

        Assert.Equal(ClipboardCaptureDisposition.SelfWrite, reader.Read(0).Disposition);

        native.Sequence++;
        Assert.Equal(ClipboardCaptureDisposition.Captured, reader.Read(0).Disposition);
    }

    [Fact]
    public void FailedTransferReleasesHandleThatWindowsDidNotOwn()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 70)
        {
            FailFormatName = ClipboardKnownFormats.RtfName,
        };
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            writer.Write(0, RichPayload(), Guid.NewGuid()));
        Assert.Equal(1, native.FreedHandleCount);
        Assert.Empty(native.TransferredFormats);
        Assert.False(native.IsOpen);
    }

    [Fact]
    public void CloseFailureDoesNotFreeHandlesAlreadyTransferredToWindows()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 75)
        {
            FailClose = true,
        };
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.Write(0, RichPayload(), Guid.NewGuid()));

        Assert.Contains("CloseClipboard", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, native.FreedHandleCount);
        Assert.Equal(4, native.TransferredFormats.Count);
        Assert.False(native.IsOpen);
    }

    [Theory]
    [InlineData(ClipboardFileTransferIntent.Copy, 1u)]
    [InlineData(ClipboardFileTransferIntent.Move, 2u)]
    public void FileWriteTransfersHdropEffectAndReceiptMarker(
        ClipboardFileTransferIntent intent,
        uint expectedEffect)
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 80);
        var known = new ClipboardKnownFormats(native);
        var instanceId = Guid.NewGuid();
        var writer = new WindowsClipboardWriter(native, known, instanceId);
        var payload = FilePayload(intent);

        var receipt = writer.Write(0, payload, Guid.NewGuid());

        Assert.Equal(
            [
                ClipboardKnownFormats.FileDrop,
                known.PreferredDropEffect.Id,
                known.InternalWriteMarker.Id,
            ],
            native.TransferredFormats);
        Assert.Equal(
            expectedEffect,
            BinaryPrimitives.ReadUInt32LittleEndian(
                native.ReadBytes(known.PreferredDropEffect.Id)));
        Assert.True(ClipboardFileDropCodec.TryDecode(
            native.ReadBytes(ClipboardKnownFormats.FileDrop),
            new ClipboardFileNormalizationOptions(),
            out var decoded));
        Assert.Equal(
            payload.FileReferences.Select(static file => file.CanonicalPath),
            decoded.Select(static file => file.CanonicalPath));
        Assert.True(writer.IsCurrent(0, receipt));
    }

    [Fact]
    public void UnknownFileIntentIsRejectedBeforeClipboardOwnershipChanges()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 90);
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            writer.Write(
                0,
                FilePayload(ClipboardFileTransferIntent.Unknown),
                Guid.NewGuid()));
        Assert.False(native.IsOpen);
        Assert.Empty(native.TransferredFormats);
    }

    [Fact]
    public void ImageWriteStreamsPngDibV5AndMarkerInCompatibilityOrder()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 100);
        var known = new ClipboardKnownFormats(native);
        var instanceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var writer = new WindowsClipboardWriter(
            native,
            known,
            instanceId,
            copyBufferBytes: 3);
        var png = Enumerable.Range(0, 17).Select(static value => (byte)value).ToArray();
        var dib = Enumerable.Range(0, 140).Select(static value => (byte)value).ToArray();
        using var materialization = new ClipboardImageReplayMaterialization(
            new string('b', 64),
            2,
            2,
            hasAlpha: true,
            dpiXMilli: 96_000,
            dpiYMilli: 96_000,
            new ByteLease(png),
            new ByteLease(dib));

        var receipt = writer.WriteImage(
            0,
            materialization,
            operationId,
            CancellationToken.None);

        Assert.Equal(
            [
                known.Png.Id,
                ClipboardKnownFormats.DibV5,
                known.InternalWriteMarker.Id,
            ],
            native.TransferredFormats);
        Assert.Equal(png, native.ReadBytes(known.Png.Id));
        Assert.Equal(dib, native.ReadBytes(ClipboardKnownFormats.DibV5));
        Assert.Equal(materialization.SemanticHash, receipt.SemanticHash);
        Assert.True(ClipboardInternalWriteMarker.TryDecode(
            native.ReadBytes(known.InternalWriteMarker.Id),
            out var marker));
        Assert.True(marker.Matches(instanceId, receipt));
        Assert.True(writer.IsCurrent(0, receipt));
    }

    [Fact]
    public void ImageMarkerFailureClearsTransferredPngAndDibWithoutDoubleFree()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 110);
        var known = new ClipboardKnownFormats(native);
        native.FailFormat = known.InternalWriteMarker.Id;
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());
        using var materialization = ImageMaterialization();

        Assert.Throws<InvalidOperationException>(() => writer.WriteImage(
            0,
            materialization,
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Equal(1, native.FreedHandleCount);
        Assert.Empty(native.TransferredFormats);
        Assert.False(native.IsOpen);
    }

    [Fact]
    public void ImageCancellationDuringChunkCopyClearsClipboardAndFreesUntransferredHandle()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 120);
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(
            native,
            known,
            Guid.NewGuid(),
            copyBufferBytes: 4);
        using var cancellation = new CancellationTokenSource();
        using var materialization = new ClipboardImageReplayMaterialization(
            new string('c', 64),
            1,
            1,
            hasAlpha: false,
            dpiXMilli: null,
            dpiYMilli: null,
            new ByteLease(
                Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray(),
                cancellation.Cancel),
            new ByteLease(new byte[128]));

        Assert.Throws<OperationCanceledException>(() => writer.WriteImage(
            0,
            materialization,
            Guid.NewGuid(),
            cancellation.Token));

        Assert.Equal(1, native.FreedHandleCount);
        Assert.Empty(native.TransferredFormats);
        Assert.False(native.IsOpen);
    }

    [Fact]
    public void ImageStreamLengthMismatchClearsClipboardAndFreesUntransferredHandle()
    {
        using var native = new WriteFakeWindowsClipboardNativeApi(sequence: 130);
        var known = new ClipboardKnownFormats(native);
        var writer = new WindowsClipboardWriter(native, known, Guid.NewGuid());
        using var materialization = new ClipboardImageReplayMaterialization(
            new string('e', 64),
            1,
            1,
            hasAlpha: false,
            dpiXMilli: null,
            dpiYMilli: null,
            new ByteLease([1, 2, 3, 4], declaredLength: 8),
            new ByteLease(new byte[128]));

        Assert.Throws<InvalidDataException>(() => writer.WriteImage(
            0,
            materialization,
            Guid.NewGuid(),
            CancellationToken.None));

        Assert.Equal(1, native.FreedHandleCount);
        Assert.Empty(native.TransferredFormats);
        Assert.False(native.IsOpen);
    }

    private static ClipboardReplayPayload RichPayload() => new(
        ClipboardContentKind.Text,
        new string('a', 64),
        [
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Html,
                300,
                "<b>Hello</b>",
                12),
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.Rtf,
                200,
                "{\\rtf1 Hello}",
                13),
            new ClipboardStoredRepresentation(
                ClipboardRepresentationKind.UnicodeText,
                100,
                "Hello",
                12),
        ]);

    private static ClipboardReplayPayload FilePayload(
        ClipboardFileTransferIntent intent)
    {
        var files = new[]
        {
            new ClipboardFileReference(0, @"C:\A.txt", @"C:\A.txt", "A.txt"),
            new ClipboardFileReference(1, @"C:\Folder", @"C:\Folder", "Folder"),
        };
        return new ClipboardReplayPayload(
            ClipboardContentKind.FileList,
            ClipboardFileSemanticIdentity.Compute(intent, files),
            [],
            intent,
            files);
    }

    private static ClipboardImageReplayMaterialization ImageMaterialization() => new(
        new string('d', 64),
        1,
        1,
        hasAlpha: false,
        dpiXMilli: null,
        dpiYMilli: null,
        new ByteLease([1, 2, 3, 4]),
        new ByteLease(new byte[128]));

    private sealed class ByteLease(
        byte[] bytes,
        Action? onFirstRead = null,
        long? declaredLength = null)
        : IClipboardPayloadLease
    {
        private int _disposed;

        public long Length => declaredLength ?? bytes.LongLength;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return onFirstRead is null
                ? new MemoryStream(bytes, writable: false)
                : new CallbackMemoryStream(bytes, onFirstRead);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class CallbackMemoryStream(byte[] bytes, Action callback)
        : MemoryStream(bytes, writable: false)
    {
        private int _called;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            if (read > 0 && Interlocked.Exchange(ref _called, 1) == 0)
            {
                callback();
            }

            return read;
        }
    }

    private sealed class WriteFakeWindowsClipboardNativeApi(uint sequence)
        : IWindowsClipboardNativeApi, IDisposable
    {
        private readonly Dictionary<string, uint> _registered = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, nint> _clipboard = [];
        private readonly Dictionary<nint, int> _allocations = [];
        private uint _nextRegistered = 0xC000;
        private bool _changed;
        private int _lastError;

        public uint Sequence { get; set; } = sequence;

        public string? FailFormatName { get; init; }

        public bool FailClose { get; init; }

        public uint? FailFormat { get; set; }

        public bool IsOpen { get; private set; }

        public int FreedHandleCount { get; private set; }

        public List<uint> TransferredFormats { get; } = [];

        public uint GetSequenceNumber() => Sequence;

        public uint RegisterFormat(string name)
        {
            if (_registered.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var value = _nextRegistered++;
            _registered.Add(name, value);
            return value;
        }

        public bool Open(nint ownerWindow)
        {
            _ = ownerWindow;
            IsOpen = true;
            return true;
        }

        public bool Close()
        {
            IsOpen = false;
            if (_changed)
            {
                Sequence++;
                _changed = false;
            }

            return !FailClose;
        }

        public bool Empty()
        {
            foreach (var handle in _clipboard.Values.Distinct())
            {
                FreeOwned(handle, countAsExplicitFree: false);
            }

            _clipboard.Clear();
            TransferredFormats.Clear();
            _changed = true;
            return true;
        }

        public uint EnumerateFormats(uint previousFormat)
        {
            var formats = _clipboard.Keys.ToArray();
            if (formats.Length == 0)
            {
                return 0;
            }

            if (previousFormat == 0)
            {
                return formats[0];
            }

            var index = Array.IndexOf(formats, previousFormat);
            return index >= 0 && index + 1 < formats.Length ? formats[index + 1] : 0;
        }

        public string? GetFormatName(uint format) => _registered
            .FirstOrDefault(pair => pair.Value == format).Key;

        public nint GetData(uint format) => _clipboard.GetValueOrDefault(format);

        public nint SetData(uint format, nint handle)
        {
            if (FailFormatName is not null &&
                _registered.TryGetValue(FailFormatName, out var failedFormat) &&
                format == failedFormat ||
                FailFormat == format)
            {
                _lastError = 5;
                return 0;
            }

            _clipboard[format] = handle;
            TransferredFormats.Add(format);
            _changed = true;
            return handle;
        }

        public nint AllocateGlobal(nuint bytes)
        {
            var pointer = Marshal.AllocHGlobal(checked((int)bytes));
            _allocations.Add(pointer, checked((int)bytes));
            return pointer;
        }

        public nint FreeGlobal(nint handle)
        {
            FreeOwned(handle, countAsExplicitFree: true);
            return 0;
        }

        public nuint GetGlobalSize(nint handle) =>
            _allocations.TryGetValue(handle, out var size) ? (nuint)size : 0;

        public nint LockGlobal(nint handle) =>
            _allocations.ContainsKey(handle) ? handle : 0;

        public void UnlockGlobal(nint handle) => _ = handle;

        public void ResetLastError() => _lastError = 0;

        public int GetLastError() => _lastError;

        public byte[] ReadBytes(uint format)
        {
            var handle = _clipboard[format];
            var bytes = new byte[_allocations[handle]];
            Marshal.Copy(handle, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            foreach (var handle in _allocations.Keys.ToArray())
            {
                FreeOwned(handle, countAsExplicitFree: false);
            }
        }

        private void FreeOwned(nint handle, bool countAsExplicitFree)
        {
            if (!_allocations.Remove(handle))
            {
                return;
            }

            Marshal.FreeHGlobal(handle);
            if (countAsExplicitFree)
            {
                FreedHandleCount++;
            }
        }
    }
}
