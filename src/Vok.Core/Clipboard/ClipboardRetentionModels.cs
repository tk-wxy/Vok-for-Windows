namespace Vok.Core.Clipboard;

public sealed class ClipboardRetentionOptions
{
    public const int DefaultMaximumUnpinnedEntryCount = 100;
    public const int MaximumSupportedEntryCount = 10_000;
    public const long DefaultMaximumTotalBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaximumSupportedAge = TimeSpan.FromDays(3650);

    public ClipboardRetentionOptions(
        int maximumUnpinnedEntryCount = DefaultMaximumUnpinnedEntryCount,
        TimeSpan? maximumAge = null,
        long maximumTotalBytes = DefaultMaximumTotalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUnpinnedEntryCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumUnpinnedEntryCount,
            MaximumSupportedEntryCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, 1);

        var resolvedMaximumAge = maximumAge ?? DefaultMaximumAge;
        if (resolvedMaximumAge <= TimeSpan.Zero ||
            resolvedMaximumAge > MaximumSupportedAge)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        MaximumUnpinnedEntryCount = maximumUnpinnedEntryCount;
        MaximumAge = resolvedMaximumAge;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public int MaximumUnpinnedEntryCount { get; }

    public TimeSpan MaximumAge { get; }

    public long MaximumTotalBytes { get; }
}

public sealed record ClipboardHistoryMaintenanceResult(
    IReadOnlyList<Guid> RemovedEntryIds,
    int RemainingEntryCount,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    int PendingAssetDeleteCount,
    bool AssetCleanupDeferred)
{
    public bool Changed => RemovedEntryIds.Count != 0;
}

public sealed record ClipboardMetadataMaintenanceResult(
    IReadOnlyList<Guid> RemovedEntryIds,
    int RemainingEntryCount,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    IReadOnlyList<ClipboardAssetDeleteClaim> PendingDeletes);
