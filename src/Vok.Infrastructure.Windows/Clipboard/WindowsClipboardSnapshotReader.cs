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
    private readonly ClipboardImageReservationBudget _imageReservations;
    private ClipboardStagingStore? _stagingStore;

    public WindowsClipboardSnapshotReader(
        IWindowsClipboardNativeApi native,
        ClipboardCaptureOptions options,
        Guid instanceId,
        Func<uint, ReadOnlyMemory<byte>, bool>? isExpectedSelfWrite = null,
        ClipboardStagingStore? stagingStore = null,
        ClipboardImageReservationBudget? imageReservations = null)
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
        _stagingStore = stagingStore;
        _imageReservations = imageReservations ??
            new ClipboardImageReservationBudget(options.Image);
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
            result.Dispose();
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

        return catalog.Any(static format => IsImage(format.RepresentationKind))
            ? CaptureImage(sequence, formats, catalog)
            : CaptureGeneral(sequence, formats, catalog);
    }

    private ClipboardCaptureResult CaptureImage(
        uint sequence,
        IReadOnlyList<uint> formats,
        IReadOnlyList<ClipboardFormatSnapshot> catalog)
    {
        var snapshots = catalog
            .Select(static entry => entry.RepresentationKind is ClipboardRepresentationKind.Bitmap
                ? Replace(entry, ClipboardFormatCaptureState.MetadataOnly)
                : entry)
            .ToList();
        try
        {
            var png = FindCandidate(
                ClipboardRepresentationKind.Png,
                formats,
                catalog);
            if (png is not null)
            {
                var pngCapture = TryCaptureImageCandidate(png.Value);
                ApplyImageCapture(snapshots, png.Value, pngCapture);
                if (pngCapture.State is ClipboardFormatCaptureState.Captured)
                {
                    return CapturedImage(sequence, snapshots);
                }

                var fallback = FindDibCandidate(formats, catalog);
                if (fallback is null)
                {
                    return UnsupportedImage(sequence, snapshots, pngCapture.Error);
                }

                var fallbackCapture = TryCaptureImageCandidate(fallback.Value);
                ApplyImageCapture(snapshots, fallback.Value, fallbackCapture);
                return fallbackCapture.State is ClipboardFormatCaptureState.Captured
                    ? CapturedImage(sequence, snapshots)
                    : UnsupportedImage(
                        sequence,
                        snapshots,
                        $"PNG candidate failed: {pngCapture.Error} " +
                        $"DIB fallback failed: {fallbackCapture.Error}");
            }

            var primary = FindDibCandidate(formats, catalog);
            if (primary is null)
            {
                return UnsupportedImage(
                    sequence,
                    snapshots,
                    "CF_BITMAP did not provide a copyable packed DIB representation.");
            }

            var primaryCapture = TryCaptureImageCandidate(primary.Value);
            ApplyImageCapture(snapshots, primary.Value, primaryCapture);
            return primaryCapture.State is ClipboardFormatCaptureState.Captured
                ? CapturedImage(sequence, snapshots)
                : UnsupportedImage(sequence, snapshots, primaryCapture.Error);
        }
        catch
        {
            DisposeFormats(snapshots);
            throw;
        }
    }

    private ClipboardCaptureResult CaptureGeneral(
        uint sequence,
        IReadOnlyList<uint> formats,
        IReadOnlyList<ClipboardFormatSnapshot> catalog)
    {
        var captured = new List<ClipboardFormatSnapshot>(catalog.Count);
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
                captured.Add(Replace(entry, state));
                hasSupportedContent |= known.Role is ClipboardFormatRole.Content;
                continue;
            }

            var payload = TryCopyGlobalMemory(known.Id, totalBytes);
            captured.Add(Replace(
                entry,
                payload.State,
                payload.Bytes,
                payload.SourceByteLength));
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

    private ImageCandidate? FindCandidate(
        ClipboardRepresentationKind kind,
        IReadOnlyList<uint> formats,
        IReadOnlyList<ClipboardFormatSnapshot> catalog)
    {
        for (var index = 0; index < catalog.Count; index++)
        {
            if (catalog[index].RepresentationKind == kind &&
                _knownFormats.TryGet(formats[index], out var known))
            {
                return new ImageCandidate(known, index);
            }
        }

        return null;
    }

    private ImageCandidate? FindDibCandidate(
        IReadOnlyList<uint> formats,
        IReadOnlyList<ClipboardFormatSnapshot> catalog)
    {
        var dibV5 = FindCandidate(
            ClipboardRepresentationKind.DibV5,
            formats,
            catalog);
        if (dibV5 is not null)
        {
            return dibV5;
        }

        var dib = FindCandidate(
            ClipboardRepresentationKind.Dib,
            formats,
            catalog);
        if (dib is not null)
        {
            return dib;
        }

        if (!catalog.Any(static format =>
                format.RepresentationKind is ClipboardRepresentationKind.Bitmap) ||
            !_knownFormats.TryGet(ClipboardKnownFormats.DibV5, out var synthesized))
        {
            return null;
        }

        return new ImageCandidate(synthesized, CatalogIndex: null);
    }

    private ImageCapture TryCaptureImageCandidate(ImageCandidate candidate)
    {
        var handle = _native.GetData(candidate.Format.Id);
        if (handle == 0)
        {
            return ImageCapture.Failed(
                $"GetClipboardData returned no {candidate.Format.Name} payload.");
        }

        var nativeSize = _native.GetGlobalSize(handle);
        var sourceByteLength = nativeSize <= unchecked((nuint)long.MaxValue)
            ? (long)nativeSize
            : (long?)null;
        if (sourceByteLength is null || nativeSize > int.MaxValue ||
            nativeSize > (nuint)_options.Image.MaximumInputBytes)
        {
            return ImageCapture.Oversize(
                sourceByteLength,
                $"{candidate.Format.Name} exceeds the image input limit.");
        }

        var size = checked((int)nativeSize);
        var requiredHeaderBytes = ClipboardImageHeaderPreflight.RequiredHeaderBytes(
            candidate.Format.RepresentationKind);
        if (size < requiredHeaderBytes)
        {
            return ImageCapture.Failed(
                $"{candidate.Format.Name} is shorter than its required header.",
                sourceByteLength);
        }

        var pointer = _native.LockGlobal(handle);
        if (pointer == 0)
        {
            return ImageCapture.Failed(
                $"GlobalLock failed for {candidate.Format.Name}.",
                sourceByteLength);
        }

        try
        {
            var headerBytes = new byte[requiredHeaderBytes];
            Marshal.Copy(pointer, headerBytes, 0, headerBytes.Length);
            if (!ClipboardImageHeaderPreflight.TryInspect(
                    candidate.Format.RepresentationKind,
                    headerBytes,
                    size,
                    _options.Image,
                    out var imageHeader,
                    out var exceededLimits,
                    out var preflightError))
            {
                return exceededLimits
                    ? ImageCapture.Oversize(sourceByteLength, preflightError)
                    : ImageCapture.Failed(preflightError, sourceByteLength);
            }

            var reservedBytes = checked(size + imageHeader!.CanonicalByteLength);
            if (!_imageReservations.TryReserve(reservedBytes, out var reservation))
            {
                return ImageCapture.CapacityExceeded(
                    sourceByteLength,
                    "The bounded image job reservation is full.");
            }

            try
            {
                if (size <= _options.Image.InlinePayloadBytes)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, size);
                    return ImageCapture.Captured(
                        bytes,
                        payloadLease: null,
                        reservation!,
                        imageHeader,
                        sourceByteLength.Value);
                }

                var payloadLease = GetStagingStore().CopyFromLockedMemory(
                    pointer,
                    size,
                    _options.Image.LeaseCopyBufferBytes);
                return ImageCapture.Captured(
                    ReadOnlyMemory<byte>.Empty,
                    payloadLease,
                    reservation!,
                    imageHeader,
                    sourceByteLength.Value);
            }
            catch (Exception exception)
            {
                reservation!.Dispose();
                return ImageCapture.Failed(
                    $"Unable to own {candidate.Format.Name}: {exception.Message}",
                    sourceByteLength);
            }
        }
        finally
        {
            _native.UnlockGlobal(handle);
        }
    }

    private ClipboardStagingStore GetStagingStore() =>
        _stagingStore ??= ClipboardStagingStore.CreateDefault();

    private static void ApplyImageCapture(
        List<ClipboardFormatSnapshot> snapshots,
        ImageCandidate candidate,
        ImageCapture capture)
    {
        try
        {
            if (candidate.CatalogIndex is { } index)
            {
                var entry = snapshots[index];
                snapshots[index] = new ClipboardFormatSnapshot(
                    entry.SourceOrder,
                    entry.FormatName,
                    entry.RepresentationKind,
                    entry.Role,
                    capture.State,
                    capture.InlinePayload,
                    capture.SourceByteLength,
                    capture.ImageHeader,
                    capture.PayloadLease,
                    capture.Reservation);
                return;
            }

            if (capture.State is ClipboardFormatCaptureState.Captured)
            {
                snapshots.Add(new ClipboardFormatSnapshot(
                    snapshots.Count,
                    candidate.Format.Name,
                    candidate.Format.RepresentationKind,
                    candidate.Format.Role,
                    capture.State,
                    capture.InlinePayload,
                    capture.SourceByteLength,
                    capture.ImageHeader,
                    capture.PayloadLease,
                    capture.Reservation));
            }
        }
        catch
        {
            capture.Dispose();
            throw;
        }
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
        var sourceByteLength = nativeSize <= unchecked((nuint)long.MaxValue)
            ? (long)nativeSize
            : (long?)null;
        if (nativeSize > int.MaxValue ||
            nativeSize > (nuint)_options.MaximumFormatBytes ||
            bytesAlreadyCaptured + (long)nativeSize > _options.MaximumSnapshotBytes)
        {
            return GlobalMemoryCopyResult.Oversize(sourceByteLength);
        }

        var size = checked((int)nativeSize);
        if (size == 0)
        {
            return GlobalMemoryCopyResult.Captured(
                ReadOnlyMemory<byte>.Empty,
                sourceByteLength);
        }

        var pointer = _native.LockGlobal(handle);
        if (pointer == 0)
        {
            return GlobalMemoryCopyResult.Failed(sourceByteLength);
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return GlobalMemoryCopyResult.Captured(bytes, sourceByteLength);
        }
        finally
        {
            _native.UnlockGlobal(handle);
        }
    }

    private static ClipboardCaptureResult CapturedImage(
        uint sequence,
        IReadOnlyList<ClipboardFormatSnapshot> formats) => new(
        sequence,
        DateTimeOffset.UtcNow,
        ClipboardCaptureDisposition.Captured,
        formats);

    private static ClipboardCaptureResult UnsupportedImage(
        uint sequence,
        IReadOnlyList<ClipboardFormatSnapshot> formats,
        string? error) => new(
        sequence,
        DateTimeOffset.UtcNow,
        ClipboardCaptureDisposition.Unsupported,
        formats,
        string.IsNullOrWhiteSpace(error)
            ? "The clipboard image has no supported payload."
            : error);

    private static ClipboardFormatSnapshot Replace(
        ClipboardFormatSnapshot format,
        ClipboardFormatCaptureState state,
        ReadOnlyMemory<byte> payload = default,
        long? sourceByteLength = null) => new(
        format.SourceOrder,
        format.FormatName,
        format.RepresentationKind,
        format.Role,
        state,
        payload,
        sourceByteLength);

    private static bool IsImage(ClipboardRepresentationKind kind) => kind is
        ClipboardRepresentationKind.Png or
        ClipboardRepresentationKind.DibV5 or
        ClipboardRepresentationKind.Dib or
        ClipboardRepresentationKind.Bitmap;

    private static void DisposeFormats(
        IEnumerable<ClipboardFormatSnapshot> formats)
    {
        foreach (var format in formats)
        {
            format.Dispose();
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

    private readonly record struct ImageCandidate(
        ClipboardKnownFormat Format,
        int? CatalogIndex);

    private readonly record struct ImageCapture(
        ClipboardFormatCaptureState State,
        ReadOnlyMemory<byte> InlinePayload,
        IClipboardPayloadLease? PayloadLease,
        IClipboardPayloadReservation? Reservation,
        ClipboardImageHeader? ImageHeader,
        long? SourceByteLength,
        string? Error)
    {
        public static ImageCapture Captured(
            ReadOnlyMemory<byte> inlinePayload,
            IClipboardPayloadLease? payloadLease,
            IClipboardPayloadReservation reservation,
            ClipboardImageHeader imageHeader,
            long sourceByteLength) => new(
            ClipboardFormatCaptureState.Captured,
            inlinePayload,
            payloadLease,
            reservation,
            imageHeader,
            sourceByteLength,
            Error: null);

        public static ImageCapture Oversize(
            long? sourceByteLength,
            string? error) => new(
            ClipboardFormatCaptureState.Oversize,
            ReadOnlyMemory<byte>.Empty,
            PayloadLease: null,
            Reservation: null,
            ImageHeader: null,
            sourceByteLength,
            error);

        public static ImageCapture CapacityExceeded(
            long? sourceByteLength,
            string error) => new(
            ClipboardFormatCaptureState.CapacityExceeded,
            ReadOnlyMemory<byte>.Empty,
            PayloadLease: null,
            Reservation: null,
            ImageHeader: null,
            sourceByteLength,
            error);

        public static ImageCapture Failed(
            string? error,
            long? sourceByteLength = null) => new(
            ClipboardFormatCaptureState.Failed,
            ReadOnlyMemory<byte>.Empty,
            PayloadLease: null,
            Reservation: null,
            ImageHeader: null,
            sourceByteLength,
            error);

        public void Dispose()
        {
            try
            {
                PayloadLease?.Dispose();
            }
            finally
            {
                Reservation?.Dispose();
            }
        }
    }

    private readonly record struct GlobalMemoryCopyResult(
        ClipboardFormatCaptureState State,
        ReadOnlyMemory<byte> Bytes,
        long? SourceByteLength)
    {
        public static GlobalMemoryCopyResult Captured(
            ReadOnlyMemory<byte> bytes,
            long? sourceByteLength = null) =>
            new(ClipboardFormatCaptureState.Captured, bytes, sourceByteLength);

        public static GlobalMemoryCopyResult Oversize(long? sourceByteLength) =>
            new(
                ClipboardFormatCaptureState.Oversize,
                ReadOnlyMemory<byte>.Empty,
                sourceByteLength);

        public static GlobalMemoryCopyResult Failed(long? sourceByteLength = null) =>
            new(
                ClipboardFormatCaptureState.Failed,
                ReadOnlyMemory<byte>.Empty,
                sourceByteLength);
    }
}
