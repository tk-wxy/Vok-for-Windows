namespace Vok.Core.Clipboard;

public sealed class ClipboardCaptureOptions
{
    public const int DefaultSnapshotQueueCapacity = 16;
    public const int DefaultMaximumFormatBytes = 64 * 1024 * 1024;
    public const int DefaultMaximumSnapshotBytes = 128 * 1024 * 1024;

    private static readonly TimeSpan[] DefaultBusyRetrySchedule =
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    public ClipboardCaptureOptions(
        int snapshotQueueCapacity = DefaultSnapshotQueueCapacity,
        int maximumFormatBytes = DefaultMaximumFormatBytes,
        int maximumSnapshotBytes = DefaultMaximumSnapshotBytes,
        IReadOnlyList<TimeSpan>? busyRetrySchedule = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFormatBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSnapshotBytes, maximumFormatBytes);

        var retries = (busyRetrySchedule ?? DefaultBusyRetrySchedule).ToArray();
        if (retries.Any(static delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyRetrySchedule),
                "Clipboard busy retry delays must be positive.");
        }

        SnapshotQueueCapacity = snapshotQueueCapacity;
        MaximumFormatBytes = maximumFormatBytes;
        MaximumSnapshotBytes = maximumSnapshotBytes;
        BusyRetrySchedule = Array.AsReadOnly(retries);
    }

    public int SnapshotQueueCapacity { get; }

    public int MaximumFormatBytes { get; }

    public int MaximumSnapshotBytes { get; }

    public IReadOnlyList<TimeSpan> BusyRetrySchedule { get; }
}
