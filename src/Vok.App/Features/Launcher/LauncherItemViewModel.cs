using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Vok.Core.Launcher;

namespace Vok.Features.Launcher;

public sealed partial class LauncherItemViewModel(LauncherItem item) : ObservableObject
{
    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDragging;

    public LauncherItem Item { get; private set; } =
        item ?? throw new ArgumentNullException(nameof(item));

    public string DisplayName => Item.DisplayName;

    public string TargetValue => Item.TargetValue;

    public bool CanRevealInExplorer => Item.CanRevealInExplorer;

    internal void UpdatePersistedOrder(long sortOrder, DateTimeOffset updatedUtc)
    {
        Item = Item with
        {
            SortOrder = sortOrder,
            Revision = checked(Item.Revision + 1),
            UpdatedUtc = updatedUtc,
        };
    }
}
