using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;

namespace Vok.Features.Stage;

internal sealed class StageCardRowsProjection : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private ObservableCollection<StageItemViewModel>? _source;
    private DispatcherOperation? _pendingRebuild;
    private long _sourceGeneration;
    private long _layoutGeneration;
    private long _contentVersion;
    private long _publishedContentVersion;
    private int _columnCount = 1;
    private bool _disposed;

    internal StageCardRowsProjection(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal ObservableCollection<StageCardRowViewModel> Rows { get; } = [];

    internal event Action? RowsPublished;
    internal long ContentVersion => _contentVersion;
    internal long PublishedContentVersion => _publishedContentVersion;
    internal long LayoutVersion => _layoutGeneration;

    internal bool HasPendingRebuild =>
        _pendingRebuild is { Status: DispatcherOperationStatus.Pending };

    internal void Attach(ObservableCollection<StageItemViewModel>? source)
    {
        VerifyAccess();
        ThrowIfDisposed();
        if (ReferenceEquals(_source, source))
        {
            return;
        }

        if (_source is not null)
        {
            _source.CollectionChanged -= OnSourceCollectionChanged;
        }

        _source = source;
        _contentVersion++;
        _sourceGeneration = checked(_sourceGeneration + 1);
        CancelPendingRebuild();
        Rows.Clear();
        if (_source is not null)
        {
            _source.CollectionChanged += OnSourceCollectionChanged;
            ScheduleRebuild();
        }
    }

    internal void SetColumnCount(int columnCount)
    {
        VerifyAccess();
        ThrowIfDisposed();
        if (columnCount is < 1 or > StageCardLayoutCalculator.MaximumColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }

        if (_columnCount == columnCount)
        {
            return;
        }

        _columnCount = columnCount;
        _layoutGeneration = checked(_layoutGeneration + 1);
        CancelPendingRebuild();
        ScheduleRebuild();
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is not null)
        {
            _source.CollectionChanged -= OnSourceCollectionChanged;
            _source = null;
        }

        _sourceGeneration = checked(_sourceGeneration + 1);
        _layoutGeneration = checked(_layoutGeneration + 1);
        CancelPendingRebuild();
        Rows.Clear();
        RowsPublished = null;
    }

    private void OnSourceCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        VerifyAccess();
        if (_disposed || !ReferenceEquals(sender, _source))
        {
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            Rows.Clear();
        }

        _contentVersion++;
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        if (_disposed || _source is null ||
            _pendingRebuild is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        var sourceGeneration = _sourceGeneration;
        var layoutGeneration = _layoutGeneration;
        _pendingRebuild = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () => PublishRows(sourceGeneration, layoutGeneration));
    }

    private void PublishRows(long sourceGeneration, long layoutGeneration)
    {
        VerifyAccess();
        _pendingRebuild = null;
        if (_disposed || _source is null ||
            sourceGeneration != _sourceGeneration ||
            layoutGeneration != _layoutGeneration)
        {
            return;
        }

        var items = _source.ToArray();
        var requiredRowCount = (items.Length + _columnCount - 1) / _columnCount;
        if (Rows.Any(row => row.ColumnCount != _columnCount))
        {
            Rows.Clear();
        }

        for (var rowIndex = 0; rowIndex < requiredRowCount; rowIndex++)
        {
            var offset = rowIndex * _columnCount;
            var rowItems = items
                .Skip(offset)
                .Take(_columnCount)
                .ToArray();
            if (rowIndex < Rows.Count)
            {
                Rows[rowIndex].UpdateItems(rowItems);
            }
            else
            {
                Rows.Add(new StageCardRowViewModel(rowItems, _columnCount));
            }
        }

        while (Rows.Count > requiredRowCount)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        _publishedContentVersion = _contentVersion;
        RowsPublished?.Invoke();
    }

    private void CancelPendingRebuild()
    {
        if (_pendingRebuild is { Status: DispatcherOperationStatus.Pending } pending)
        {
            _ = pending.Abort();
        }

        _pendingRebuild = null;
    }

    private void VerifyAccess()
    {
        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Stage card rows must be projected on their owning Dispatcher.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
