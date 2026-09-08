namespace Vok.Features.Stage;

public sealed class StageCardRowViewModel(
    IReadOnlyList<StageItemViewModel> items,
    int columnCount)
{
    public IReadOnlyList<StageItemViewModel> Items { get; } =
        items ?? throw new ArgumentNullException(nameof(items));

    public int ColumnCount { get; } = columnCount > 0
        ? columnCount
        : throw new ArgumentOutOfRangeException(nameof(columnCount));
}
