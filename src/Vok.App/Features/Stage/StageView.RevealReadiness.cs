using Vok.Hosting;

namespace Vok.Features.Stage;

public partial class StageView
{
    private long _activeRevealGeneration;
    private long _completedRevealGeneration;
    private long _staticPresentationVersion;
    private bool _observingRevealReadiness;

    internal event EventHandler<WorkspaceRevealReadyEventArgs>? RevealReady;

    internal void BeginReveal(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        CancelRevealCore();
        CancelEdgePrefetch();
        _activeRevealGeneration = generation;
        _completedRevealGeneration = 0;
        _availabilityRefreshGeneration = generation;
        _availabilityRequestsAllowed = false;
        CancelAllAvailabilityBindings(resetState: false);
        LayoutUpdated += OnRevealReadinessLayoutUpdated;
        _observingRevealReadiness = true;
        EvaluateRevealReadiness();
    }

    internal bool IsRevealReady(WorkspaceRevealReadinessToken token)
    {
        if (token.Generation <= 0 ||
            token.Generation != _activeRevealGeneration)
        {
            return false;
        }

        var state = CaptureRevealState();
        return state.StaticContentReady &&
            state.ContentVersion == token.ContentVersion &&
            state.LayoutVersion == token.LayoutVersion &&
            _staticPresentationVersion == token.StateVersion;
    }

    internal void CompleteReveal(long generation)
    {
        if (generation <= 0 || generation != _activeRevealGeneration)
        {
            return;
        }

        DetachRevealReadinessObservation();
        _activeRevealGeneration = 0;
        _completedRevealGeneration = generation;
        _availabilityRefreshGeneration = generation;
        _availabilityRequestsAllowed = true;
        TraceStage("StageRevealCompleted");
        ScheduleAvailabilityRefresh();
        ScheduleEdgePrefetch();
    }

    internal void CancelReveal(long generation)
    {
        if (generation <= 0 ||
            (generation != _activeRevealGeneration &&
             generation != _completedRevealGeneration))
        {
            return;
        }

        CancelRevealCore();
        CancelEdgePrefetch();
        _completedRevealGeneration = 0;
        _availabilityRequestsAllowed = false;
        CancelAllAvailabilityBindings(resetState: false);
    }

    private void OnRevealReadinessLayoutUpdated(object? sender, EventArgs e) =>
        EvaluateRevealReadiness();

    private void EvaluateRevealReadiness()
    {
        try
        {
            var generation = _activeRevealGeneration;
            if (generation <= 0 || !_observingRevealReadiness)
            {
                return;
            }

            var state = CaptureRevealState();
            if (!state.StaticContentReady)
            {
                return;
            }

            // Claim before publishing so reentrant or duplicate layout notifications cannot
            // publish a second terminal readiness event for the same attempt.
            DetachRevealReadinessObservation();
            var token = new WorkspaceRevealReadinessToken(
                generation,
                state.ContentVersion,
                state.LayoutVersion,
                _staticPresentationVersion);
            TraceStage("StageStaticContentReady");
            RevealReady?.Invoke(this, new WorkspaceRevealReadyEventArgs(token));
        }
        catch (Exception exception)
        {
            _revealTrace.Record(
                "StageReadinessFailed",
                _activeRevealGeneration,
                _revealSourceId,
                detail: exception.GetType().Name);
        }
    }

    private void CancelRevealCore()
    {
        DetachRevealReadinessObservation();
        _activeRevealGeneration = 0;
    }

    private void DetachRevealReadinessObservation()
    {
        if (_observingRevealReadiness)
        {
            LayoutUpdated -= OnRevealReadinessLayoutUpdated;
        }

        _observingRevealReadiness = false;
    }
}
