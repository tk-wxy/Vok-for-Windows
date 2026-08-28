using Vok.Core.Platform;

namespace Vok.Features.Launcher;

public sealed class LauncherLayoutProfileCache
{
    private const double CacheKeyScale = 1024.0;
    private readonly Dictionary<long, LauncherLayoutProfile> _profiles = new();

    internal int CalculationCount { get; private set; }

    internal void Prewarm(IEnumerable<DisplayWorkArea> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);

        foreach (var workArea in workAreas)
        {
            if (workArea.HeightPixels <= 0)
            {
                continue;
            }

            _ = GetOrCreateForWorkArea(workArea.HeightDip);
        }
    }

    internal LauncherLayoutProfile GetOrCreateForWorkArea(double workAreaHeight) =>
        GetOrCreateForViewport(LauncherLayoutCalculator.GetViewportHeight(workAreaHeight));

    internal LauncherLayoutProfile GetOrCreateForViewport(double viewportHeight)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportHeight),
                viewportHeight,
                null);
        }

        var cacheKey = checked((long)Math.Round(
            viewportHeight * CacheKeyScale,
            MidpointRounding.AwayFromZero));
        if (_profiles.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        var calculated = LauncherLayoutCalculator.CalculateForViewport(viewportHeight);
        _profiles.Add(cacheKey, calculated);
        CalculationCount++;
        return calculated;
    }
}
