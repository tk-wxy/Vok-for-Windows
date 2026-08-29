using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardImageReservationBudget : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maximumJobs;
    private readonly long _maximumBytes;
    private int _currentJobs;
    private long _currentBytes;
    private bool _disposed;

    public ClipboardImageReservationBudget(ClipboardImageCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maximumJobs = options.ImageWorkerCapacity;
        _maximumBytes = options.MaximumReservedBytes;
    }

    public int CurrentJobCount
    {
        get
        {
            lock (_gate)
            {
                return _currentJobs;
            }
        }
    }

    public long CurrentReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    public bool TryReserve(
        long reservedBytes,
        out IClipboardPayloadReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reservedBytes, 1);
        lock (_gate)
        {
            if (_disposed || _currentJobs >= _maximumJobs ||
                reservedBytes > _maximumBytes - _currentBytes)
            {
                reservation = null;
                return false;
            }

            _currentJobs++;
            _currentBytes += reservedBytes;
            reservation = new Reservation(this, reservedBytes);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _currentJobs = 0;
            _currentBytes = 0;
        }
    }

    private void Release(long reservedBytes)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _currentJobs--;
            _currentBytes -= reservedBytes;
        }
    }

    private sealed class Reservation(
        ClipboardImageReservationBudget owner,
        long reservedBytes) : IClipboardPayloadReservation
    {
        private ClipboardImageReservationBudget? _owner = owner;

        public long ReservedBytes { get; } = reservedBytes;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(ReservedBytes);
    }
}
