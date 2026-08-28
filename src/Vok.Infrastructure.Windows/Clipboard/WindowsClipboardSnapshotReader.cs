using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class WindowsClipboardSnapshotReader
{
    private const int ClipboardBusyError = 5;

    private readonly IWindowsClipboardNativeApi _native;
    private readonly ClipboardCaptureOptions _options;
    private readonly ClipboardKnownFormats _knownFormats;
    private readonly Func<uint, ReadOnlyMemory<byte>, bool> _isExpectedSelfWrite;

    public WindowsClipboardSnapshotReader(
        IWindowsClipboardNativeApi native,
        ClipboardCaptureOptions options,
        Guid instanceId,
        Func<uint, ReadOnlyMemory<byte>, bool>? isExpectedSelfWrite = null)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(options);
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        }

        _native = native;
        _options = options;
        _knownFormats = new ClipboardKnownFormats(native);
        var legacyMarker = instanceId.ToByteArray();
        _isExpectedSelfWrite = isExpectedSelfWrite ?? ((_, marker) =>
            marker.Span.SequenceEqual(legacyMarker));
    }

    internal ClipboardKnownFormats KnownFormats => _knownFormats;

    public ClipboardCaptureResult Read(nint ownerWindow)
    {
        var sequence = _native.GetSequenceNumber();
        if (!_native.Open(ownerWindow))
        {
            var error = _native.GetLastError();
            return new ClipboardCaptureResult(
                sequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Busy,
                [],
                error == ClipboardBusyError
                    ? "The clipboard is busy."
                    : $"OpenClipboard failed with Win32 error {error}.");
        }

        ClipboardCaptureResult result;
        try
        {
            result = ReadOpenedClipboard();
        }
        catch (Exception exception)
        {
            result = Failed(sequence, "Clipboard snapshot failed.", exception);
        }

        if (!_native.Close())
        {
            return Failed(
                result.RuntimeSequence,
                $"CloseClipboard failed with Win32 error {_native.GetLastError()}.");
        }

        return result;
    }

    private ClipboardCaptureResult ReadOpenedClipboard()
    {
        var sequence = _native.GetSequenceNumber();
        var formats = EnumerateFormats();
        if (formats.Count == 0)
        {
            return new ClipboardCaptureResult(
                sequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Empty,
                []);
        }

        var catalog = formats
            .Select((format, index) => CreateCatalogEntry(format, index))
            .ToArray();

        if (formats.Contains(_knownFormats.ExcludeFromMonitor.Id) ||
            ShouldExcludeFromHistory(formats))
        {
            return new ClipboardCaptureResult(
                sequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.Excluded,
                CatalogOnly(catalog));
        }

        if (IsMatchingSelfWrite(sequence, formats))
        {
            return new ClipboardCaptureResult(
                sequence,
                DateTimeOffset.UtcNow,
                ClipboardCaptureDisposition.SelfWrite,
                CatalogOnly(catalog));
        }

        var captured = new List<ClipboardFormatSnapshot>(catalog.Length);
        var totalBytes = 0L;
        var hasSupportedContent = false;
        foreach (var entry in catalog)
        {
            if (!_knownFormats.TryGet(formats[entry.SourceOrder], out var known) ||
                known.Role is ClipboardFormatRole.Privacy or ClipboardFormatRole.Internal)
            {
                captured.Add(entry);
                continue;
            }

            if (!known.UsesGlobalMemory)
            {
                var state = known.Role is
                    ClipboardFormatRole.Content or ClipboardFormatRole.VirtualFile
                    ? ClipboardFormatCaptureState.MetadataOnly
                    : ClipboardFormatCaptureState.NotRequested;
                captured.Add(entry with { CaptureState = state });
                hasSupportedContent |= known.Role is ClipboardFormatRole.Content;
                continue;
            }

            var payload = TryCopyGlobalMemory(known.Id, totalBytes);
            captured.Add(entry with
            {
                CaptureState = payload.State,
                Payload = payload.Bytes,
            });
            if (payload.State is ClipboardFormatCaptureState.Captured)
            {
                totalBytes += payload.Bytes.Length;
                hasSupportedContent |= known.Role is ClipboardFormatRole.Content;
            }
        }

        return new ClipboardCaptureResult(
            sequence,
            DateTimeOffset.UtcNow,
            hasSupportedContent
                ? ClipboardCaptureDisposition.Captured
                : ClipboardCaptureDisposition.Unsupported,
            captured.AsReadOnly());
    }

    private List<uint> EnumerateFormats()
    {
        var formats = new List<uint>();
        var current = 0u;
        while (true)
        {
            _native.ResetLastError();
            var next = _native.EnumerateFormats(current);
            if (next == 0)
            {
                var error = _native.GetLastError();
                if (error != 0)
                {
                    throw new InvalidOperationException(
                        $"EnumClipboardFormats failed with Win32 error {error}.");
                }

                break;
            }

            formats.Add(next);
            current = next;
        }

        return formats;
    }

    private ClipboardFormatSnapshot CreateCatalogEntry(uint format, int sourceOrder)
    {
        if (_knownFormats.TryGet(format, out var known))
        {
            return new ClipboardFormatSnapshot(
                sourceOrder,
                known.Name,
                known.RepresentationKind,
                known.Role,
                ClipboardFormatCaptureState.NotRequested,
                ReadOnlyMemory<byte>.Empty);
        }

        return new ClipboardFormatSnapshot(
            sourceOrder,
            _native.GetFormatName(format) ?? $"FORMAT_{format}",
            ClipboardRepresentationKind.Unknown,
            ClipboardFormatRole.Unknown,
            ClipboardFormatCaptureState.NotRequested,
            ReadOnlyMemory<byte>.Empty);
    }

    private bool ShouldExcludeFromHistory(IReadOnlyCollection<uint> formats)
    {
        if (!formats.Contains(_knownFormats.IncludeInHistory.Id))
        {
            return false;
        }

        var marker = TryCopyGlobalMemory(
            _knownFormats.IncludeInHistory.Id,
            bytesAlreadyCaptured: 0);
        if (marker.State is not ClipboardFormatCaptureState.Captured ||
            marker.Bytes.Length < sizeof(uint))
        {
            return true;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(marker.Bytes.Span) == 0;
    }

    private bool IsMatchingSelfWrite(
        uint sequence,
        IReadOnlyCollection<uint> formats)
    {
        if (!formats.Contains(_knownFormats.InternalWriteMarker.Id))
        {
            return false;
        }

        var marker = TryCopyGlobalMemory(
            _knownFormats.InternalWriteMarker.Id,
            bytesAlreadyCaptured: 0);
        return marker.State is ClipboardFormatCaptureState.Captured &&
               _isExpectedSelfWrite(sequence, marker.Bytes);
    }

    private GlobalMemoryCopyResult TryCopyGlobalMemory(
        uint format,
        long bytesAlreadyCaptured)
    {
        var handle = _native.GetData(format);
        if (handle == 0)
        {
            return GlobalMemoryCopyResult.Failed();
        }

        var nativeSize = _native.GetGlobalSize(handle);
        if (nativeSize > int.MaxValue ||
            nativeSize > (nuint)_options.MaximumFormatBytes ||
            bytesAlreadyCaptured + (long)nativeSize > _options.MaximumSnapshotBytes)
        {
            return GlobalMemoryCopyResult.Oversize();
        }

        var size = checked((int)nativeSize);
        if (size == 0)
        {
            return GlobalMemoryCopyResult.Captured(ReadOnlyMemory<byte>.Empty);
        }

        var pointer = _native.LockGlobal(handle);
        if (pointer == 0)
        {
            return GlobalMemoryCopyResult.Failed();
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return GlobalMemoryCopyResult.Captured(bytes);
        }
        finally
        {
            _native.UnlockGlobal(handle);
        }
    }

    private static IReadOnlyList<ClipboardFormatSnapshot> CatalogOnly(
        IEnumerable<ClipboardFormatSnapshot> formats) =>
        Array.AsReadOnly(formats.ToArray());

    private static ClipboardCaptureResult Failed(
        uint sequence,
        string message,
        Exception? exception = null) =>
        new(
            sequence,
            DateTimeOffset.UtcNow,
            ClipboardCaptureDisposition.Failed,
            [],
            exception is null ? message : $"{message} {exception.Message}");

    private readonly record struct GlobalMemoryCopyResult(
        ClipboardFormatCaptureState State,
        ReadOnlyMemory<byte> Bytes)
    {
        public static GlobalMemoryCopyResult Captured(ReadOnlyMemory<byte> bytes) =>
            new(ClipboardFormatCaptureState.Captured, bytes);

        public static GlobalMemoryCopyResult Oversize() =>
            new(ClipboardFormatCaptureState.Oversize, ReadOnlyMemory<byte>.Empty);

        public static GlobalMemoryCopyResult Failed() =>
            new(ClipboardFormatCaptureState.Failed, ReadOnlyMemory<byte>.Empty);
    }
}
