using System.Windows;
using Vok.Core.Diagnostics;

namespace Vok.Features.Stage;

public partial class StageView
{
    private RevealTrace _revealTrace = RevealTrace.Disabled;
    private long _revealSourceId;
    private long _diagnosticGeneration;
    private StageRevealState? _lastRevealState;
    private bool _observingRevealLayout;

    internal void ConfigureRevealDiagnostics(RevealTrace trace)
    {
        DetachRevealDiagnostics();
        _revealTrace = trace;
        _revealSourceId = trace.NextId();
        if (_observedItems is not null) TraceStage("StageSourceAttached");
        if (IsLoaded) AttachRevealLayoutObservation();
    }

    internal void DetachRevealDiagnostics()
    {
        if (_observingRevealLayout) LayoutUpdated -= OnRevealLayoutUpdated;
        _observingRevealLayout = false;
    }

    private void AttachRevealLayoutObservation()
    {
        if (!_revealTrace.Enabled || _observingRevealLayout) return;
        LayoutUpdated += OnRevealLayoutUpdated;
        _observingRevealLayout = true;
    }

    internal void ObserveReveal(string phase, long generation)
    {
        if (!_revealTrace.Enabled) return;
        if (phase == "ShowBegin") _diagnosticGeneration = generation;
        _revealTrace.Record("StageCheckpoint", generation, _revealSourceId,
            detail: phase, stage: CaptureRevealState());
    }

    private void TraceStage(string phase)
    {
        if (_revealTrace.Enabled)
            _revealTrace.Record(phase, _diagnosticGeneration, _revealSourceId,
                value1: _cardRowsProjection.ContentVersion,
                value2: _cardRowsProjection.LayoutVersion);
    }

    private void OnRevealLayoutUpdated(object? sender, EventArgs e)
    {
        // No UpdateLayout/Dispatcher pump: observing must not force the missing work to finish.
        try
        {
            var state = CaptureRevealState();
            if (state == _lastRevealState) return;
            _lastRevealState = state;
            _revealTrace.Record("StageLayoutObserved", _diagnosticGeneration, _revealSourceId,
                stage: state);
        }
        catch (Exception exception)
        {
            _revealTrace.Record("StageObservationFailed", _diagnosticGeneration,
                _revealSourceId, detail: exception.GetType().Name);
        }
    }

    internal StageRevealState CaptureRevealState()
    {
        var viewModel = DataContext as StageViewModel;
        var scrollViewer = GetStageScrollViewer();
        var width = scrollViewer?.ViewportWidth ?? 0;
        var height = scrollViewer?.ViewportHeight ?? 0;
        var offset = scrollViewer?.VerticalOffset ?? 0;
        var pendingProjection = _cardRowsProjection.HasPendingRebuild ||
            _cardRowsProjection.ContentVersion != _cardRowsProjection.PublishedContentVersion;
        var pendingScroll = _rowPublicationRefreshPending || _scrollResetPending ||
            _pendingScrollAnchor is not null;
        var expectedIds = new HashSet<Guid>();
        if (CardRowUnitHeight > 0 && height > 0)
        {
            for (var index = 0; index < CardRows.Count; index++)
            {
                var top = index * CardRowUnitHeight + (CardRowUnitHeight - CardHeight) / 2;
                if (top < offset + height && top + CardHeight > offset)
                    foreach (var item in CardRows[index].Items) expectedIds.Add(item.Id);
            }
        }
        var actualIds = new HashSet<Guid>();
        var allCardsValid = true;
        foreach (var card in _availabilityCards)
        {
            if (!IsCardInViewport(card)) continue;
            if (card.DataContext is StageItemViewModel item) actualIds.Add(item.Id);
            allCardsValid &= card.IsMeasureValid && card.IsArrangeValid;
        }
        // Order-independent identity fingerprint. Counts and the full set comparison remain separate.
        ulong hash = 0;
        Span<byte> bytes = stackalloc byte[16];
        foreach (var id in actualIds)
        {
            id.TryWriteBytes(bytes);
            ulong itemHash = 14695981039346656037;
            foreach (var value in bytes) itemHash = unchecked((itemHash ^ value) * 1099511628211);
            hash ^= itemHash;
        }
        var empty = viewModel?.IsEmpty == true;
        var error = viewModel?.ShowErrorState == true;
        var layoutValid = IsMeasureValid && IsArrangeValid &&
            StageItemsList.IsMeasureValid && StageItemsList.IsArrangeValid && allCardsValid;
        var ready = IsLoaded && IsVisible && width > 0 && height > 0 && layoutValid &&
            !_cardLayoutRefreshPending && !pendingProjection && !pendingScroll &&
            viewModel is { IsLoading: false } &&
            (empty || error || (expectedIds.Count > 0 && expectedIds.SetEquals(actualIds)));
        return new(_cardRowsProjection.ContentVersion, _cardRowsProjection.LayoutVersion,
            viewModel?.Items.Count ?? 0, CardRows.Count, expectedIds.Count, actualIds.Count,
            hash, width, height, offset, pendingProjection, _cardLayoutRefreshPending,
            pendingScroll, IsMeasureValid && StageItemsList.IsMeasureValid,
            IsArrangeValid && StageItemsList.IsArrangeValid, empty, error,
            viewModel?.IsLoading == true, ready);
    }
}
