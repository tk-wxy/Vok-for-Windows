using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Vok.Features.Stage;

public partial class StageView : UserControl
{
    private INotifyCollectionChanged? _observedItems;
    private bool _externalDragActive;
    private bool _externalDragTargeted;
    private bool _scrollResetPending;

    public StageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    internal void SetExternalDragState(bool sessionActive, bool targeted)
    {
        if (_externalDragActive == sessionActive &&
            _externalDragTargeted == targeted)
        {
            return;
        }

        _externalDragActive = sessionActive;
        _externalDragTargeted = targeted;
        DropOverlay.Visibility = sessionActive && targeted
            ? Visibility.Visible
            : Visibility.Collapsed;
        StageInteractionSurface.IsHitTestVisible = !sessionActive;
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged -= OnItemsCollectionChanged;
        }

        _observedItems = (e.NewValue as StageViewModel)?.Items;
        if (_observedItems is not null)
        {
            _observedItems.CollectionChanged += OnItemsCollectionChanged;
        }
    }

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not NotifyCollectionChangedAction.Reset || _scrollResetPending)
        {
            return;
        }

        _scrollResetPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollResetPending = false;
            FindVisualDescendant<ScrollViewer>(StageItemsList)?.ScrollToTop();
        });
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
