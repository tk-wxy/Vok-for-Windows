namespace Vok.Features.Stage;

internal sealed record StageCardLayoutProfile(
    int ColumnCount,
    double CardWidth,
    double CardHeight,
    double RowUnitHeight,
    double HorizontalGap,
    double VerticalGap,
    double SideInset);
