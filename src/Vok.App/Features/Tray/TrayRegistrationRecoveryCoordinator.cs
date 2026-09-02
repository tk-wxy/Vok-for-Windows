using System.Diagnostics;
using Vok.Core.Platform;

namespace Vok.Features.Tray;

internal enum TrayRegistrationRecoveryTrigger
{
    Startup,
    TaskbarCreated,
    PowerResume,
    SessionUnlock,
    Watchdog,
}

internal enum TrayRegistrationRecoveryState
{
    NotStarted,
    Healthy,
    Suspect,
    Recovering,
    Degraded,
    Disposed,
}

internal sealed class TrayRegistrationRecoveryOptions
{
    public static TrayRegistrationRecoveryOptions Default { get; } = new(
        watchdogInterval: TimeSpan.FromMinutes(1),
        watchdogConfirmationDelay: TimeSpan.FromMilliseconds(250),
        retryDelays:
        [
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(10),
        ]);

    public TrayRegistrationRecoveryOptions(
        TimeSpan watchdogInterval,
        TimeSpan watchdogConfirmationDelay,
        IReadOnlyList<TimeSpan> retryDelays)
    {
        if (watchdogInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogInterval));
        }

        if (watchdogConfirmationDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogConfirmationDelay));
        }

        ArgumentNullException.ThrowIfNull(retryDelays);
        if (retryDelays.Any(static delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelays));
        }

        WatchdogInterval = watchdogInterval;
        WatchdogConfirmationDelay = watchdogConfirmationDelay;
        RetryDelays = retryDelays.ToArray();
    }

    public TimeSpan WatchdogInterval { get; }

    public TimeSpan WatchdogConfirmationDelay { get; }

    public IReadOnlyList<TimeSpan> RetryDelays { get; }
}

internal interface ITrayRecoveryScheduler : IDisposable
{
    void Schedule(TimeSpan delay, Action callback);

    void Cancel();
}

internal sealed class TrayRegistrationRecoveryCoordinator : IDisposable
{
    private readonly ITrayRegistrationProbe _probe;
    private readonly nint _ownerWindow;
    private readonly Guid _iconId;
    private readonly Action _repair;
    private readonly ITrayRecoveryScheduler _scheduler;
    private readonly TrayRegistrationRecoveryOptions _options;
    private readonly Action<Exception> _reportFault;
    private readonly List<Exception> _episodeFailures = [];
    private TrayRegistrationRecoveryTrigger? _pendingTrigger;
    private TrayRegistrationRecoveryTrigger _episodeTrigger;
    private TrayRegistrationRecoveryState _state;
    private long _generation;
    private int _repairAttempts;
    private int? _lastNativeResult;
    private bool _started;
    private bool _blocked;
    private bool _executing;
    private bool _disposed;

    public TrayRegistrationRecoveryCoordinator(
        ITrayRegistrationProbe probe,
        nint ownerWindow,
        Guid iconId,
        Action repair,
        ITrayRecoveryScheduler scheduler,
        TrayRegistrationRecoveryOptions options,
        Action<Exception> reportFault)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        if (ownerWindow == 0)
        {
            throw new ArgumentException(
                "The tray owner window is unavailable.",
                nameof(ownerWindow));
        }

        if (iconId == Guid.Empty)
        {
            throw new ArgumentException(
                "The tray icon identifier is unavailable.",
                nameof(iconId));
        }

        _ownerWindow = ownerWindow;
        _iconId = iconId;
        _repair = repair ?? throw new ArgumentNullException(nameof(repair));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reportFault = reportFault ?? throw new ArgumentNullException(nameof(reportFault));
        _state = TrayRegistrationRecoveryState.NotStarted;
    }

    public TrayRegistrationRecoveryState State => _state;

    public long Generation => _generation;

    public int RepairAttempts => _repairAttempts;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        RequestCheck(TrayRegistrationRecoveryTrigger.Startup);
    }

    public void RequestCheck(TrayRegistrationRecoveryTrigger trigger)
    {
        if (_disposed || !_started)
        {
            return;
        }

        if (_blocked || _executing)
        {
            RecordPendingTrigger(trigger);
            return;
        }

        Execute(trigger, () => BeginCheck(trigger));
    }

    public void SetBlocked(bool blocked)
    {
        if (_disposed || _blocked == blocked)
        {
            return;
        }

        _blocked = blocked;
        if (!blocked)
        {
            DrainPendingTrigger();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _blocked = false;
        _pendingTrigger = null;
        _generation++;
        _state = TrayRegistrationRecoveryState.Disposed;
        _scheduler.Cancel();
        _scheduler.Dispose();
        _episodeFailures.Clear();
    }

    private void BeginCheck(TrayRegistrationRecoveryTrigger trigger)
    {
        _scheduler.Cancel();
        var generation = ++_generation;
        _episodeTrigger = trigger;
        _repairAttempts = 0;
        _lastNativeResult = null;
        _episodeFailures.Clear();

        var observation = Probe();
        if (observation.IsRegistered)
        {
            CompleteHealthy(generation);
            return;
        }

        TraceMissing(trigger, observation);
        if (trigger == TrayRegistrationRecoveryTrigger.Watchdog)
        {
            _state = TrayRegistrationRecoveryState.Suspect;
            Schedule(
                generation,
                _options.WatchdogConfirmationDelay,
                trigger,
                () => ConfirmWatchdogMiss(generation));
            return;
        }

        AttemptRepair(generation);
    }

    private void ConfirmWatchdogMiss(long generation)
    {
        var observation = Probe();
        if (observation.IsRegistered)
        {
            CompleteHealthy(generation);
            return;
        }

        TraceMissing(_episodeTrigger, observation);
        AttemptRepair(generation);
    }

    private void AttemptRepair(long generation)
    {
        _state = TrayRegistrationRecoveryState.Recovering;
        _repairAttempts++;

        Exception? repairFailure = null;
        try
        {
            _repair();
        }
        catch (Exception exception)
        {
            repairFailure = exception;
            _episodeFailures.Add(exception);
        }

        var observation = Probe();
        if (observation.IsRegistered)
        {
            if (repairFailure is not null)
            {
                Trace.TraceWarning(
                    "Tray registration repair reported an exception but the Shell postcondition succeeded: {0}",
                    repairFailure);
            }

            CompleteHealthy(generation);
            return;
        }

        if (observation.Failure is not null)
        {
            _episodeFailures.Add(observation.Failure);
        }
        else
        {
            _episodeFailures.Add(new InvalidOperationException(
                $"The Shell still did not expose tray icon {_iconId:B} after repair " +
                $"attempt {_repairAttempts}; native result " +
                $"0x{observation.NativeResult.GetValueOrDefault():X8}."));
        }

        if (_repairAttempts > _options.RetryDelays.Count)
        {
            ExhaustRecovery(generation);
            return;
        }

        var delay = _options.RetryDelays[_repairAttempts - 1];
        Schedule(
            generation,
            delay,
            _episodeTrigger,
            () => AttemptRepair(generation));
    }

    private ProbeObservation Probe()
    {
        try
        {
            var result = _probe.Probe(_ownerWindow, _iconId);
            _lastNativeResult = result.NativeResult;
            return new ProbeObservation(
                result.IsRegistered,
                result.NativeResult,
                Failure: null);
        }
        catch (Exception exception)
        {
            return new ProbeObservation(
                IsRegistered: false,
                NativeResult: null,
                Failure: exception);
        }
    }

    private void CompleteHealthy(long generation)
    {
        if (_repairAttempts > 0)
        {
            Trace.TraceInformation(
                "Tray registration recovered after {0} attempt(s); trigger={1}, icon={2:B}.",
                _repairAttempts,
                _episodeTrigger,
                _iconId);
        }

        _state = TrayRegistrationRecoveryState.Healthy;
        _episodeFailures.Clear();
        Schedule(
            generation,
            _options.WatchdogInterval,
            TrayRegistrationRecoveryTrigger.Watchdog,
            () => BeginCheck(TrayRegistrationRecoveryTrigger.Watchdog));
    }

    private void ExhaustRecovery(long generation)
    {
        _state = TrayRegistrationRecoveryState.Degraded;
        var failures = _episodeFailures.ToArray();
        Schedule(
            generation,
            _options.WatchdogInterval,
            TrayRegistrationRecoveryTrigger.Watchdog,
            () => BeginCheck(TrayRegistrationRecoveryTrigger.Watchdog));

        var innerException = failures.Length switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
        _reportFault(new TrayRegistrationRecoveryException(
            _episodeTrigger,
            _repairAttempts,
            _lastNativeResult,
            innerException));
    }

    private void Schedule(
        long generation,
        TimeSpan delay,
        TrayRegistrationRecoveryTrigger trigger,
        Action callback)
    {
        _scheduler.Schedule(delay, () =>
        {
            if (_disposed || !_started || generation != _generation)
            {
                return;
            }

            if (_blocked || _executing)
            {
                RecordPendingTrigger(trigger);
                return;
            }

            Execute(trigger, callback);
        });
    }

    private void Execute(TrayRegistrationRecoveryTrigger trigger, Action action)
    {
        _executing = true;
        try
        {
            action();
        }
        finally
        {
            _executing = false;
        }

        DrainPendingTrigger();
    }

    private void DrainPendingTrigger()
    {
        if (_disposed || !_started || _blocked || _executing ||
            _pendingTrigger is not { } trigger)
        {
            return;
        }

        _pendingTrigger = null;
        Execute(trigger, () => BeginCheck(trigger));
    }

    private void RecordPendingTrigger(TrayRegistrationRecoveryTrigger trigger)
    {
        if (_pendingTrigger is null ||
            _pendingTrigger == TrayRegistrationRecoveryTrigger.Watchdog &&
            trigger != TrayRegistrationRecoveryTrigger.Watchdog)
        {
            _pendingTrigger = trigger;
        }
    }

    private void TraceMissing(
        TrayRegistrationRecoveryTrigger trigger,
        ProbeObservation observation)
    {
        if (observation.Failure is not null)
        {
            Trace.TraceWarning(
                "Unable to query tray registration; trigger={0}, icon={1:B}: {2}",
                trigger,
                _iconId,
                observation.Failure);
            return;
        }

        Trace.TraceWarning(
            "The Shell does not expose tray icon {0:B}; trigger={1}, nativeResult=0x{2:X8}.",
            _iconId,
            trigger,
            observation.NativeResult.GetValueOrDefault());
    }

    private readonly record struct ProbeObservation(
        bool IsRegistered,
        int? NativeResult,
        Exception? Failure);
}

internal sealed class TrayRegistrationRecoveryException : Exception
{
    public TrayRegistrationRecoveryException(
        TrayRegistrationRecoveryTrigger trigger,
        int attempts,
        int? nativeResult,
        Exception? innerException)
        : base(
            $"Unable to restore the Windows Shell tray registration after {attempts} " +
            $"attempt(s); trigger={trigger}, nativeResult=" +
            (nativeResult is null ? "unavailable" : $"0x{nativeResult.Value:X8}"),
            innerException)
    {
        Trigger = trigger;
        Attempts = attempts;
        NativeResult = nativeResult;
    }

    public TrayRegistrationRecoveryTrigger Trigger { get; }

    public int Attempts { get; }

    public int? NativeResult { get; }
}
