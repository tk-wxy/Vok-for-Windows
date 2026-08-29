using System.Runtime.ExceptionServices;

namespace Vok.Core.Clipboard;

public enum ClipboardCaptureDisposition
{
    Captured = 0,
    Empty = 1,
    Excluded = 2,
    SelfWrite = 3,
    Unsupported = 4,
    Busy = 5,
    Failed = 6,
}

public enum ClipboardPayloadStorageKind
{
    None = 0,
    Inline = 1,
    LeasedBlob = 2,
}

public interface IClipboardPayloadLease : IDisposable
{
    long Length { get; }

    Stream OpenRead();
}

public interface IClipboardPayloadReservation : IDisposable
{
    long ReservedBytes { get; }
}

public sealed record ClipboardImageHeader(
    int PixelWidth,
    int PixelHeight,
    long CanonicalByteLength);

public sealed class ClipboardFormatSnapshot : IDisposable
{
    private IClipboardPayloadLease? _payloadLease;
    private IClipboardPayloadReservation? _reservation;
    private int _disposed;

    public ClipboardFormatSnapshot(
        int SourceOrder,
        string FormatName,
        ClipboardRepresentationKind RepresentationKind,
        ClipboardFormatRole Role,
        ClipboardFormatCaptureState CaptureState,
        ReadOnlyMemory<byte> Payload,
        long? SourceByteLength = null,
        ClipboardImageHeader? ImageHeader = null,
        IClipboardPayloadLease? PayloadLease = null,
        IClipboardPayloadReservation? Reservation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(SourceOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(FormatName);
        if (SourceByteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceByteLength));
        }

        if (!Payload.IsEmpty && PayloadLease is not null)
        {
            throw new ArgumentException(
                "Clipboard payload cannot be both inline and leased.",
                nameof(PayloadLease));
        }

        if (PayloadLease is not null && PayloadLease.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PayloadLease));
        }

        this.SourceOrder = SourceOrder;
        this.FormatName = FormatName;
        this.RepresentationKind = RepresentationKind;
        this.Role = Role;
        this.CaptureState = CaptureState;
        this.Payload = Payload;
        this.SourceByteLength = SourceByteLength;
        this.ImageHeader = ImageHeader;
        _payloadLease = PayloadLease;
        _reservation = Reservation;
    }

    public int SourceOrder { get; }

    public string FormatName { get; }

    public ClipboardRepresentationKind RepresentationKind { get; }

    public ClipboardFormatRole Role { get; }

    public ClipboardFormatCaptureState CaptureState { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public long? SourceByteLength { get; }

    public ClipboardImageHeader? ImageHeader { get; }

    public ClipboardPayloadStorageKind PayloadStorageKind =>
        _payloadLease is not null
            ? ClipboardPayloadStorageKind.LeasedBlob
            : !Payload.IsEmpty
                ? ClipboardPayloadStorageKind.Inline
                : ClipboardPayloadStorageKind.None;

    public long PayloadByteLength => _payloadLease?.Length ?? Payload.Length;

    public int ByteLength => checked((int)PayloadByteLength);

    public long ReservedByteLength => _reservation?.ReservedBytes ?? 0;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Stream OpenPayloadReadStream()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var lease = Volatile.Read(ref _payloadLease);
        if (lease is not null)
        {
            return lease.OpenRead();
        }

        return new MemoryStream(Payload.ToArray(), writable: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var lease = Interlocked.Exchange(ref _payloadLease, null);
        var reservation = Interlocked.Exchange(ref _reservation, null);
        Exception? failure = null;
        try
        {
            lease?.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            reservation?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

public sealed class ClipboardCaptureResult : IDisposable
{
    private int _disposed;

    public ClipboardCaptureResult(
        uint RuntimeSequence,
        DateTimeOffset CapturedUtc,
        ClipboardCaptureDisposition Disposition,
        IReadOnlyList<ClipboardFormatSnapshot> Formats,
        string? Error = null)
    {
        ArgumentNullException.ThrowIfNull(Formats);
        this.RuntimeSequence = RuntimeSequence;
        this.CapturedUtc = CapturedUtc;
        this.Disposition = Disposition;
        this.Formats = Array.AsReadOnly(Formats.ToArray());
        this.Error = Error;
    }

    public uint RuntimeSequence { get; }

    public DateTimeOffset CapturedUtc { get; }

    public ClipboardCaptureDisposition Disposition { get; }

    public IReadOnlyList<ClipboardFormatSnapshot> Formats { get; }

    public string? Error { get; }

    public bool HasCapturedContent =>
        Disposition is ClipboardCaptureDisposition.Captured &&
        Formats.Any(static format =>
            format.Role is ClipboardFormatRole.Content &&
            format.CaptureState is ClipboardFormatCaptureState.Captured or
                ClipboardFormatCaptureState.MetadataOnly);

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        foreach (var format in Formats)
        {
            try
            {
                format.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
