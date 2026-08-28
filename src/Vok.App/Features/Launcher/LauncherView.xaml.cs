using System.Windows;
using System.Windows.Controls;

namespace Vok.Features.Launcher;

public partial class LauncherView : UserControl
{
    public static readonly DependencyProperty CardSurfaceHeightProperty =
        DependencyProperty.Register(
            nameof(CardSurfaceHeight),
            typeof(double),
            typeof(LauncherView),
            new PropertyMetadata(94.0));

    public static readonly DependencyProperty CardUnitHeightProperty =
        DependencyProperty.Register(
            nameof(CardUnitHeight),
            typeof(double),
            typeof(LauncherView),
            new PropertyMetadata(100.0));

    public static readonly DependencyProperty VisibleRowCountProperty =
        DependencyProperty.Register(
            nameof(VisibleRowCount),
            typeof(int),
            typeof(LauncherView),
            new PropertyMetadata(1));

    private LauncherLayoutProfileCache? _layoutProfiles;

    public LauncherView()
    {
        InitializeComponent();
        LauncherItemsViewport.SizeChanged += OnLauncherItemsViewportSizeChanged;
    }

    public double CardSurfaceHeight
    {
        get => (double)GetValue(CardSurfaceHeightProperty);
        private set => SetCurrentValue(CardSurfaceHeightProperty, value);
    }

    public double CardUnitHeight
    {
        get => (double)GetValue(CardUnitHeightProperty);
        private set => SetCurrentValue(CardUnitHeightProperty, value);
    }

    public int VisibleRowCount
    {
        get => (int)GetValue(VisibleRowCountProperty);
        private set => SetCurrentValue(VisibleRowCountProperty, value);
    }

    internal void UseLayoutProfiles(LauncherLayoutProfileCache layoutProfiles)
    {
        ArgumentNullException.ThrowIfNull(layoutProfiles);
        _layoutProfiles = layoutProfiles;
        ApplyLayoutProfile(LauncherItemsViewport.ActualHeight);
    }

    private void OnLauncherItemsViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyLayoutProfile(e.NewSize.Height);

    private void ApplyLayoutProfile(double viewportHeight)
    {
        if (_layoutProfiles is null || viewportHeight <= 0)
        {
            return;
        }

        var profile = _layoutProfiles.GetOrCreateForViewport(viewportHeight);
        CardSurfaceHeight = profile.CardSurfaceHeight;
        CardUnitHeight = profile.CardUnitHeight;
        VisibleRowCount = profile.VisibleRowCount;
    }
}
