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
    }

    [Fact]
    public void BitmapIsCataloguedWithoutTreatingItsHandleAsGlobalMemory()
    {
        using var native = new FakeWindowsClipboardNativeApi { Sequence = 63 };
        var reader = CreateReader(native);
        native.SetFormats((ClipboardKnownFormats.Bitmap, null));

        var result = reader.Read(0);

        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        var bitmap = Assert.Single(result.Formats);
        Assert.Equal(ClipboardRepresentationKind.Bitmap, bitmap.RepresentationKind);
        Assert.Equal(ClipboardFormatCaptureState.MetadataOnly, bitmap.CaptureState);
        Assert.Empty(native.DataReads);
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
