namespace Vok.Core.Diagnostics;

public interface IPerformanceSampler
{
    PerformanceSnapshot Capture();
}
