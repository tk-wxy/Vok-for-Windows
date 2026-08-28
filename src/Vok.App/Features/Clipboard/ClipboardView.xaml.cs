using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vok.Features.Clipboard;

public partial class ClipboardView : UserControl
{
    private ScrollViewer? _scrollViewer;

    public ClipboardView()
    {
        InitializeComponent();
        ClipboardItemsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardItemViewModel item } ||
            DataContext is not ClipboardViewModel viewModel)
        {
            return;
        }

        await viewModel.RequestRemoveAsync(item);
    }

    private async void OnItemsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ClipboardViewModel viewModel ||
            ClipboardItemsList.SelectedItem is not ClipboardItemViewModel item)
        {
            return;
        }

        if (e.Key is Key.Enter && viewModel.PasteCommand.CanExecute(item))
        {
            e.Handled = true;
            await viewModel.PasteCommand.ExecuteAsync(item);
        }
        else if (e.Key is Key.Delete)
        {
            e.Handled = true;
            await viewModel.RequestRemoveAsync(item);
        }
    }

    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scrollViewer ??= FindVisualDescendant<ScrollViewer>(ClipboardItemsList);
        if (_scrollViewer is null ||
            DataContext is not ClipboardViewModel viewModel ||
            !viewModel.HasMore ||
            viewModel.IsLoading ||
            _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset > 160)
        {
            return;
        }

        await viewModel.LoadMoreAsync();
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
