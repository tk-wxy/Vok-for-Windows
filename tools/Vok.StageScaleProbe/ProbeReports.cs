using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vok.StageScaleProbe;

internal sealed record ProbeMarker(
    int MarkerVersion,
    string CreatedUtc,
    string SourceRevision,
    int ItemCount,
    string DatabaseFileName,
    string SourceFileName,
    string SourceSha256,
    string StageDigest);

internal sealed record DatabaseAudit(
    long UserVersion,
    long StageCount,
    string QuickCheck,
    string StageDigest,
    bool StageDigestRecomputed,
    long DatabaseBytes,
    long WalBytes);

internal sealed record PrepareProbeReport(
    int ReportVersion,
    string Status,
    string Mode,
    string StartedUtc,
    string CompletedUtc,
    string SourceRevision,
    string ExecutablePath,
    string ExecutableSha256,
    string Root,
    int RequestedItemCount,
    int SeedBatchSize,
    double SeedElapsedMilliseconds,
    string SourceSha256,
    DatabaseAudit Database);

internal sealed record DisplaySnapshot(
    double WindowWidthDip,
    double WindowHeightDip,
    double ViewportWidthDip,
    double ViewportHeightDip,
    int ColumnCount,
    uint WindowDpi,
    double DpiScale,
    int ScreenPixelWidth,
    int ScreenPixelHeight,
    int WorkAreaPixelWidth,
    int WorkAreaPixelHeight);

internal sealed record ResourceSample(
    long Index,
    string TimestampUtc,
    string Phase,
    int Step,
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    long ManagedCommittedBytes,
    int HandleCount,
    int ThreadCount,
    uint UserObjectCount,
    uint GdiObjectCount);

internal sealed record MetricSummary(
    int Samples,
    double First,
    double Last,
    double Minimum,
    double Maximum,
    double TailSlopePerSample);

internal sealed record ResourceSummary(
    MetricSummary WorkingSetBytes,
    MetricSummary PrivateBytes,
    MetricSummary ManagedHeapBytes,
    MetricSummary ManagedCommittedBytes,
    MetricSummary HandleCount,
    MetricSummary ThreadCount,
    MetricSummary UserObjectCount,
    MetricSummary GdiObjectCount);

internal sealed record FrameIntervalSummary(
    int Samples,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds);

internal sealed record EdgeStepSample(
    int Step,
    string Direction,
    double ElapsedMilliseconds,
    double ProjectionChangingMilliseconds,
    double ItemsChangedMilliseconds,
    double RowsChangedMilliseconds,
    double ViewportSettledMilliseconds,
    int WindowItemCount,
    int WindowMaximumItemCount,
    int CardRowCount,
    int RealizedCardCount,
    double VerticalOffset,
    double ScrollableHeight,
    double? AnchorErrorDip,
    Guid FirstItemId,
    Guid LastItemId,
    bool CanLoadNewer,
    bool CanLoadOlder);

internal sealed record InspectionTransitionSample(
    int Step,
    string InteractionPhase,
    string Direction,
    double ElapsedMilliseconds,
    double? ProjectionChangingMilliseconds,
    double? ItemsChangedMilliseconds,
    double? RowsChangedMilliseconds,
    double? ViewportSettledMilliseconds,
    bool MilestonesComplete,
    int WindowItemCount,
    int WindowMaximumItemCount,
    int CardRowCount,
    int RealizedCardCount,
    double VerticalOffset,
    double ScrollableHeight,
    double? AnchorErrorDip,
    Guid FirstItemId,
    Guid LastItemId,
    bool CanLoadNewer,
    bool CanLoadOlder);

internal sealed record InspectionSummary(
    int WarmupRequested,
    int WarmupCompleted,
    int ManualTransitions,
    int ManualOlderTransitions,
    int ManualNewerTransitions,
    int VisitedOccurrenceCount,
    bool OrderingValid,
    bool WindowBoundValid,
    bool DeferredWorkDrained,
    bool OverlappingPrefetchObserved,
    int IncompletePrefetchCount,
    int AnchorMeasurements,
    double MaximumAnchorErrorDip,
    int MaximumWindowItemCount,
    int MaximumRealizedCardCount,
    bool ObjectiveChecksPassed,
    bool Exact4K100Environment,
    bool PhysicalGateEligible,
    string UserVerdict);

internal sealed record TraversalSummary(
    string Workload,
    int RequestedEdgeSteps,
    int CompletedEdgeSteps,
    int OlderSteps,
    int NewerSteps,
    int VisitedOccurrenceCount,
    bool ReachedOldest,
    bool ReturnedToNewest,
    bool OrderingValid,
    bool WindowBoundValid,
    bool DeferredWorkDrained,
    int AnchorMeasurements,
    double MaximumAnchorErrorDip,
    int MaximumWindowItemCount,
    int MaximumRealizedCardCount,
    double EdgeP50Milliseconds,
    double EdgeP95Milliseconds,
    double EdgeMaximumMilliseconds);

internal sealed record RunProbeReport(
    int ReportVersion,
    string Status,
    string Mode,
    string StartedUtc,
    string CompletedUtc,
    string SourceRevision,
    string ExecutablePath,
    string ExecutableSha256,
    string Root,
    int RequestedItemCount,
    double ViewModelInitializeMilliseconds,
    double FirstSettledViewportMilliseconds,
    DisplaySnapshot Display,
    DatabaseAudit DatabaseBefore,
    DatabaseAudit DatabaseAfter,
    string SourceSha256Before,
    string SourceSha256After,
    TraversalSummary Traversal,
    FrameIntervalSummary Frames,
    ResourceSummary Resources,
    IReadOnlyList<EdgeStepSample> EdgeSamples,
    IReadOnlyList<ResourceSample> ResourceSamples);

internal sealed record InspectionProbeReport(
    int ReportVersion,
    string Status,
    string Mode,
    string StartedUtc,
    string CompletedUtc,
    string SourceRevision,
    string ExecutablePath,
    string ExecutableSha256,
    string Root,
    int RequestedItemCount,
    string ExitReason,
    double ViewModelInitializeMilliseconds,
    double FirstSettledViewportMilliseconds,
    DisplaySnapshot Display,
    DatabaseAudit DatabaseBefore,
    DatabaseAudit DatabaseAfter,
    string SourceSha256Before,
    string SourceSha256After,
    InspectionSummary Inspection,
    FrameIntervalSummary Frames,
    ResourceSummary Resources,
    IReadOnlyList<InspectionTransitionSample> Transitions,
    IReadOnlyList<ResourceSample> ResourceSamples);

internal sealed record FailureProbeReport(
    int ReportVersion,
    string Status,
    string Mode,
    string FailedUtc,
    string? SourceRevision,
    string ErrorType,
    string ErrorMessage,
    string? StackTrace);

internal static class ProbeReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static void WriteNew(string path, object report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"The report directory does not exist: {parent}");
        }

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        JsonSerializer.Serialize(stream, report, report.GetType(), Options);
    }

    internal static T Read<T>(string path) where T : notnull
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options) ??
            throw new InvalidDataException($"The JSON file was empty: {path}");
    }
}
