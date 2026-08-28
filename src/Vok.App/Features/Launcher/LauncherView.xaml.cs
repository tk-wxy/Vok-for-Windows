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
    private bool _pageLoadRequested;

    public LauncherView()
    {
        InitializeComponent();
        LauncherItemsViewport.SizeChanged += OnLauncherItemsViewportSizeChanged;
        LauncherItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnLauncherScrollChanged));
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

    internal void SetExternalDragActive(bool isActive, bool canDrop)
    {
        DropOverlay.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        DropOverlayText.Text = canDrop
            ? "松手添加到启动台"
            : "移动到启动台后松手";
        DropOverlay.Opacity = canDrop ? 0.96 : 0.72;
    }

    private void OnLauncherItemsViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyLayoutProfile(e.NewSize.Height);

    private async void OnLauncherScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_pageLoadRequested ||
            DataContext is not LauncherViewModel viewModel ||
            e.ExtentHeight <= 0 ||
            e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - CardUnitHeight)
        {
            return;
        }

        _pageLoadRequested = true;
        try
        {
            await viewModel.LoadNextPageAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // The application is closing; the view model owns cancellation and cleanup.
        }
        catch (OperationCanceledException)
        {
            // The view-model lifetime ended while an incremental page was pending.
        }
        finally
        {
            _pageLoadRequested = false;
        }
    }

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
