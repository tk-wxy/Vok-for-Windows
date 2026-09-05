namespace Vok.Features.Stage;

internal enum StageCardLayoutTier
{
    Compact,
    ManagementSafe,
}

internal sealed record StageCardLayoutProfile(
    StageCardLayoutTier Tier,
    int ColumnCount,
    double CardWidth,
    double CardHeight,
    double RowUnitHeight,
    double HorizontalGap,
    double VerticalGap,
    double SideInset);
