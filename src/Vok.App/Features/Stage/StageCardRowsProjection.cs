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
    private int _columnCount = 1;
    private bool _disposed;

    internal StageCardRowsProjection(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal ObservableCollection<StageCardRowViewModel> Rows { get; } = [];

    internal event Action? RowsPublished;

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
        var rows = new List<StageCardRowViewModel>(
            (items.Length + _columnCount - 1) / _columnCount);
        for (var index = 0; index < items.Length; index += _columnCount)
        {
            rows.Add(new StageCardRowViewModel(
                items.Skip(index).Take(_columnCount).ToArray(),
                _columnCount));
        }

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

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
