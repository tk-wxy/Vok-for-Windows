namespace Vok.Features.Launcher;

public sealed class LauncherRowViewModel(IReadOnlyList<LauncherItemViewModel> items)
{
    public IReadOnlyList<LauncherItemViewModel> Items { get; } =
        items ?? throw new ArgumentNullException(nameof(items));
}
