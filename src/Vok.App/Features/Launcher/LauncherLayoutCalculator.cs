namespace Vok.Features.Launcher;

internal static class LauncherLayoutCalculator
{
    internal const double HeaderHeight = 56.0;
    internal const double BottomBarHeight = 36.0;
    internal const double SectionHeaderHeight = 24.0;
    internal const double ViewportTopInset = 6.0;
    internal const double ViewportBottomInset = 8.0;
    internal const double TargetCardUnitHeight = 100.0;
    internal const double MinimumCardUnitHeight = 94.0;
    internal const double MaximumCardUnitHeight = 116.0;
    internal const double CardVerticalInset = 6.0;

    internal static double GetViewportHeight(double workAreaHeight)
    {
        ValidatePositiveFinite(workAreaHeight, nameof(workAreaHeight));
        return Math.Max(
            1.0,
            workAreaHeight - HeaderHeight - BottomBarHeight - SectionHeaderHeight -
            ViewportTopInset - ViewportBottomInset);
    }

    internal static LauncherLayoutProfile CalculateForWorkArea(double workAreaHeight) =>
        CalculateForViewport(GetViewportHeight(workAreaHeight));

    internal static LauncherLayoutProfile CalculateForViewport(double viewportHeight)
    {
        ValidatePositiveFinite(viewportHeight, nameof(viewportHeight));

        var maximumRowsThatPreserveContent = Math.Max(
            1,
            (int)Math.Floor(viewportHeight / MinimumCardUnitHeight));
        var minimumRowsThatAvoidOversizing = Math.Max(
            1,
            (int)Math.Ceiling(viewportHeight / MaximumCardUnitHeight));
        var idealRows = Math.Max(
            1,
            (int)Math.Round(
                viewportHeight / TargetCardUnitHeight,
                MidpointRounding.AwayFromZero));

        var visibleRows = minimumRowsThatAvoidOversizing <= maximumRowsThatPreserveContent
            ? Math.Clamp(
                idealRows,
                minimumRowsThatAvoidOversizing,
                maximumRowsThatPreserveContent)
            : maximumRowsThatPreserveContent;
        var cardUnitHeight = viewportHeight / visibleRows;

        return new LauncherLayoutProfile(
            viewportHeight,
            visibleRows,
            cardUnitHeight,
            Math.Max(1.0, cardUnitHeight - CardVerticalInset));
    }

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }
    }
}
