using System.Diagnostics;
using System.Runtime.InteropServices;
using Vok.Core.Diagnostics;

namespace Vok.Infrastructure.Windows.Diagnostics;

public sealed partial class ProcessPerformanceSampler : IPerformanceSampler
{
    private const uint GdiObjects = 0;
    private const uint UserObjects = 1;

    public PerformanceSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new PerformanceSnapshot(
            DateTimeOffset.UtcNow,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count,
            GetGuiResources(process.Handle, UserObjects),
            GetGuiResources(process.Handle, GdiObjects));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint processHandle, uint flags);
}
