namespace Vok.Core.Diagnostics;

public readonly record struct PerformanceSnapshot(
    DateTimeOffset CapturedAt,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int HandleCount,
    int ThreadCount,
    uint UserObjectCount,
    uint GdiObjectCount);
