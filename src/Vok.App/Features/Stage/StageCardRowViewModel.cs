using System.Collections.ObjectModel;

namespace Vok.Features.Stage;

public sealed class StageCardRowViewModel
{
    private readonly ObservableCollection<StageItemViewModel> _items;

    public StageCardRowViewModel(
        IReadOnlyList<StageItemViewModel> items,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);
        _items = new ObservableCollection<StageItemViewModel>(items);
        ColumnCount = columnCount;
    }

    public IReadOnlyList<StageItemViewModel> Items => _items;

    public int ColumnCount { get; }

    internal void UpdateItems(IReadOnlyList<StageItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, ColumnCount);
        if (items.Count == 0)
        {
            throw new ArgumentException("A Stage card row cannot be empty.", nameof(items));
        }

        var sharedCount = Math.Min(_items.Count, items.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            if (!ReferenceEquals(_items[index], items[index]))
            {
                _items[index] = items[index];
            }
        }

        while (_items.Count > items.Count)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        for (var index = sharedCount; index < items.Count; index++)
        {
            _items.Add(items[index]);
        }
    }
}
