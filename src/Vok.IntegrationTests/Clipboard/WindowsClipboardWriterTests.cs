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

            return true;
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
                format == failedFormat)
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
