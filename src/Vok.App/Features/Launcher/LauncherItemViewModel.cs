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

    public LauncherItem Item { get; } =
        item ?? throw new ArgumentNullException(nameof(item));

    public string DisplayName => Item.DisplayName;

    public string TargetValue => Item.TargetValue;

    public bool CanRevealInExplorer => Item.CanRevealInExplorer;
}
