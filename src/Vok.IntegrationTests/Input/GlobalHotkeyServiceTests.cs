using System.Collections.Concurrent;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Input;
using Xunit;

namespace Vok.IntegrationTests.Input;

public sealed class GlobalHotkeyServiceTests
{
    private static readonly GlobalHotkeyGesture FirstGesture = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt,
        0x76);
    private static readonly GlobalHotkeyGesture SecondGesture = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift,
        0x77);

    [Fact]
    public async Task RealWin32RegistrationCanBeSuspendedAndReconfigured()
    {
        var initial = new GlobalHotkeyGesture(
            GlobalHotkeyModifiers.Control |
            GlobalHotkeyModifiers.Alt |
            GlobalHotkeyModifiers.Shift,
            0x86);
        var replacement = new GlobalHotkeyGesture(
            GlobalHotkeyModifiers.Control |
            GlobalHotkeyModifiers.Alt |
            GlobalHotkeyModifiers.Shift,
            0x87);
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            initial);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.SuspendAsync(TestContext.Current.CancellationToken);
        await service.ChangeGestureAsync(
            replacement,
            TestContext.Current.CancellationToken);

        Assert.Equal(replacement, service.CurrentGesture);
    }

    [Fact]
    public async Task SuspendAndResumeAreSerializedOnTheMessageThread()
    {
        var native = new FakeNativeMethods();
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FirstGesture, native.RegisteredGesture);

        await service.SuspendAsync(TestContext.Current.CancellationToken);
        Assert.Null(native.RegisteredGesture);

        await service.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FirstGesture, native.RegisteredGesture);
    }

    [Fact]
    public async Task ConflictingReplacementRestoresPreviousRegistration()
    {
        var native = new FakeNativeMethods();
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        await service.StartAsync(TestContext.Current.CancellationToken);
        native.RejectedGestures.Add(SecondGesture);

        var failure = await Assert.ThrowsAsync<GlobalHotkeyRegistrationException>(
            () => service.ChangeGestureAsync(
                SecondGesture,
                TestContext.Current.CancellationToken));

        Assert.Equal(SecondGesture, failure.Gesture);
        Assert.Equal(FirstGesture, service.CurrentGesture);
        Assert.Equal(FirstGesture, native.RegisteredGesture);
        Assert.Equal(
            [FirstGesture, SecondGesture, FirstGesture],
            native.RegisterAttempts);
    }

    [Fact]
    public async Task RegistrationCanRecoverAfterInitialConflict()
    {
        var native = new FakeNativeMethods();
        native.RejectedGestures.Add(FirstGesture);
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);

        await Assert.ThrowsAsync<GlobalHotkeyRegistrationException>(
            () => service.StartAsync(TestContext.Current.CancellationToken));

        await service.ChangeGestureAsync(
            SecondGesture,
            TestContext.Current.CancellationToken);

        Assert.Equal(SecondGesture, service.CurrentGesture);
        Assert.Equal(SecondGesture, native.RegisteredGesture);
    }

    [Fact]
    public async Task HotkeyMessageCapturesForegroundBeforePublishing()
    {
        var foreground = new FakeForegroundWindowService();
        var native = new FakeNativeMethods();
        using var service = new GlobalHotkeyService(foreground, FirstGesture, native);
        var pressed = new TaskCompletionSource<HotkeyPressStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PressChanged += (_, pressEvent) =>
        {
            if (pressEvent is HotkeyPressStarted started)
            {
                pressed.TrySetResult(started);
            }
        };
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        var invocation = await pressed.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(foreground.Target, invocation.ForegroundTarget);
        Assert.Equal(1, foreground.CaptureCount);
    }

    [Fact]
    public async Task ReleaseIsPublishedWhenAnyConfiguredKeyIsNoLongerDown()
    {
        var native = new FakeNativeMethods();
        native.SetGestureDown(FirstGesture);
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        var started = new TaskCompletionSource<HotkeyPressStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<HotkeyPressReleased>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PressChanged += (_, pressEvent) =>
        {
            if (pressEvent is HotkeyPressStarted start)
            {
                started.TrySetResult(start);
            }
            else if (pressEvent is HotkeyPressReleased release)
            {
                released.TrySetResult(release);
            }
        };
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        var start = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual((nuint)0, native.ActiveTimerId);
        Assert.False(released.Task.IsCompleted);

        native.SetKeyDown(0x11, isDown: false);
        native.PostTimer();
        var release = await released.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(start.PressGeneration, release.PressGeneration);
        Assert.True(release.HeldDuration >= TimeSpan.Zero);
        Assert.Equal((nuint)0, native.ActiveTimerId);
    }

    [Fact]
    public async Task AlreadyReleasedFastPressCompletesWithoutWaitingForTimerTick()
    {
        var native = new FakeNativeMethods();
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        var events = new List<HotkeyPressEvent>();
        var released = new TaskCompletionSource<HotkeyPressReleased>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PressChanged += (_, pressEvent) =>
        {
            lock (events)
            {
                events.Add(pressEvent);
            }

            if (pressEvent is HotkeyPressReleased release)
            {
                released.TrySetResult(release);
            }
        };
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        await released.Task.WaitAsync(TestContext.Current.CancellationToken);

        lock (events)
        {
            Assert.Collection(
                events,
                pressEvent => Assert.IsType<HotkeyPressStarted>(pressEvent),
                pressEvent => Assert.IsType<HotkeyPressReleased>(pressEvent));
        }
        Assert.Equal((nuint)0, native.ActiveTimerId);
    }

    [Fact]
    public async Task SuspendCancelsActivePressBeforeUnregistering()
    {
        var native = new FakeNativeMethods();
        native.SetGestureDown(FirstGesture);
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        var started = new TaskCompletionSource<HotkeyPressStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<HotkeyPressCanceled>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PressChanged += (_, pressEvent) =>
        {
            if (pressEvent is HotkeyPressStarted start)
            {
                started.TrySetResult(start);
            }
            else if (pressEvent is HotkeyPressCanceled cancel)
            {
                canceled.TrySetResult(cancel);
            }
        };
        await service.StartAsync(TestContext.Current.CancellationToken);
        native.PostHotkey();
        var start = await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await service.SuspendAsync(TestContext.Current.CancellationToken);
        var cancel = await canceled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(start.PressGeneration, cancel.PressGeneration);
        Assert.Equal((nuint)0, native.ActiveTimerId);
        Assert.Null(native.RegisteredGesture);
    }

    [Fact]
    public async Task TimerStartFailureFallsBackToACompletedShortPressAndReportsFault()
    {
        var native = new FakeNativeMethods { FailTimerStart = true };
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        var events = new List<HotkeyPressEvent>();
        var released = new TaskCompletionSource<HotkeyPressReleased>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.PressChanged += (_, pressEvent) =>
        {
            lock (events)
            {
                events.Add(pressEvent);
            }

            if (pressEvent is HotkeyPressReleased release)
            {
                released.TrySetResult(release);
            }
        };
        service.Faulted += (_, exception) => faulted.TrySetResult(exception);
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        var release = await released.Task.WaitAsync(TestContext.Current.CancellationToken);
        var failure = await faulted.Task.WaitAsync(TestContext.Current.CancellationToken);

        lock (events)
        {
            Assert.Collection(
                events,
                pressEvent => Assert.IsType<HotkeyPressStarted>(pressEvent),
                pressEvent => Assert.IsType<HotkeyPressReleased>(pressEvent));
        }
        Assert.True(release.HeldDuration <= HotkeyPressSemantics.TapMaxDuration);
        Assert.Contains("release detector", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FirstGesture, native.RegisteredGesture);
        Assert.Equal((nuint)0, native.ActiveTimerId);
    }

    [Fact]
    public async Task NewNativeDownCancelsPressWhoseReleaseWasNotObserved()
    {
        var native = new FakeNativeMethods();
        native.SetGestureDown(FirstGesture);
        using var service = new GlobalHotkeyService(
            new FakeForegroundWindowService(),
            FirstGesture,
            native);
        var events = new List<HotkeyPressEvent>();
        var firstStarted = new TaskCompletionSource<HotkeyPressStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<HotkeyPressStarted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        service.PressChanged += (_, pressEvent) =>
        {
            lock (events)
            {
                events.Add(pressEvent);
            }

            if (pressEvent is HotkeyPressStarted started)
            {
                var count = Interlocked.Increment(ref startedCount);
                _ = count == 1
                    ? firstStarted.TrySetResult(started)
                    : secondStarted.TrySetResult(started);
            }
        };
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        native.PostHotkey();
        var second = await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        lock (events)
        {
            Assert.Collection(
                events,
                pressEvent => Assert.IsType<HotkeyPressStarted>(pressEvent),
                pressEvent => Assert.IsType<HotkeyPressCanceled>(pressEvent),
                pressEvent => Assert.Same(second, pressEvent));
            Assert.Equal(
                events[0].PressGeneration + 1,
                events[2].PressGeneration);
        }
        Assert.NotEqual((nuint)0, native.ActiveTimerId);
    }

    private sealed class FakeNativeMethods : IGlobalHotkeyNativeMethods
    {
        private readonly BlockingCollection<HotkeyThreadMessage> _messages = [];

        public uint HotkeyMessage => 0x0312;

        public uint TimerMessage => 0x0113;

        public HashSet<GlobalHotkeyGesture> RejectedGestures { get; } = [];

        public List<GlobalHotkeyGesture> RegisterAttempts { get; } = [];

        public GlobalHotkeyGesture? RegisteredGesture { get; private set; }

        public int LastError { get; private set; }

        public nuint ActiveTimerId { get; private set; }

        public bool FailTimerStart { get; init; }

        public ConcurrentDictionary<uint, byte> DownKeys { get; } = [];

        public uint GetCurrentThreadId() => 42;

        public void EnsureMessageQueue()
        {
        }

        public bool RegisterHotkey(int id, GlobalHotkeyGesture gesture)
        {
            RegisterAttempts.Add(gesture);
            if (RejectedGestures.Contains(gesture) || RegisteredGesture is not null)
            {
                LastError = 1409;
                return false;
            }

            RegisteredGesture = gesture;
            return true;
        }

        public bool UnregisterHotkey(int id)
        {
            if (RegisteredGesture is null)
            {
                LastError = 1419;
                return false;
            }

            RegisteredGesture = null;
            return true;
        }

        public nuint StartReleaseTimer(uint intervalMilliseconds)
        {
            Assert.Equal(
                GlobalHotkeyService.ReleasePollIntervalMilliseconds,
                intervalMilliseconds);
            ActiveTimerId = FailTimerStart ? 0 : (nuint)73;
            return ActiveTimerId;
        }

        public bool StopReleaseTimer(nuint timerId)
        {
            if (ActiveTimerId != timerId)
            {
                return false;
            }

            ActiveTimerId = 0;
            return true;
        }

        public bool IsKeyDown(uint virtualKey) => DownKeys.ContainsKey(virtualKey);

        public HotkeyThreadMessage GetMessage() => _messages.Take();

        public bool PostCommand(uint threadId, uint message, nuint commandId)
        {
            _messages.Add(new HotkeyThreadMessage(1, message, commandId));
            return true;
        }

        public bool PostQuit(uint threadId)
        {
            _messages.Add(new HotkeyThreadMessage(0, 0, 0));
            return true;
        }

        public int GetLastError() => LastError;

        public void PostHotkey() =>
            _messages.Add(new HotkeyThreadMessage(1, HotkeyMessage, 0x564F));

        public void PostTimer()
        {
            var timerId = ActiveTimerId;
            Assert.NotEqual((nuint)0, timerId);
            _messages.Add(new HotkeyThreadMessage(1, TimerMessage, timerId));
        }

        public void SetGestureDown(GlobalHotkeyGesture gesture)
        {
            SetKeyDown(gesture.VirtualKey, isDown: true);
            if (gesture.Modifiers.HasFlag(GlobalHotkeyModifiers.Control))
            {
                SetKeyDown(0x11, isDown: true);
            }
            if (gesture.Modifiers.HasFlag(GlobalHotkeyModifiers.Alt))
            {
                SetKeyDown(0x12, isDown: true);
            }
            if (gesture.Modifiers.HasFlag(GlobalHotkeyModifiers.Shift))
            {
                SetKeyDown(0x10, isDown: true);
            }
            if (gesture.Modifiers.HasFlag(GlobalHotkeyModifiers.Windows))
            {
                SetKeyDown(0x5B, isDown: true);
            }
        }

        public void SetKeyDown(uint virtualKey, bool isDown)
        {
            if (isDown)
            {
                DownKeys[virtualKey] = 0;
            }
            else
            {
                DownKeys.TryRemove(virtualKey, out _);
            }
        }
    }

    private sealed class FakeForegroundWindowService : IForegroundWindowService
    {
        public ForegroundTarget Target { get; } = new(
            0x123,
            10,
            20,
            0x124,
            10,
            20,
            ForegroundWindowRelationship.Descendant);

        public int CaptureCount { get; private set; }

        public ForegroundTarget Capture()
        {
            CaptureCount++;
            return Target;
        }

        public ForegroundRestoreResult Restore(ForegroundTarget target) =>
            new(ForegroundRestoreStatus.NotRequested, 0);
    }
}
