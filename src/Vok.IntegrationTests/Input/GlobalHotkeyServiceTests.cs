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
        var pressed = new TaskCompletionSource<HotkeyInvocation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pressed += (_, invocation) => pressed.TrySetResult(invocation);
        await service.StartAsync(TestContext.Current.CancellationToken);

        native.PostHotkey();
        var invocation = await pressed.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(foreground.Target, invocation.ForegroundTarget);
        Assert.Equal(1, foreground.CaptureCount);
    }

    private sealed class FakeNativeMethods : IGlobalHotkeyNativeMethods
    {
        private readonly BlockingCollection<HotkeyThreadMessage> _messages = [];

        public uint HotkeyMessage => 0x0312;

        public HashSet<GlobalHotkeyGesture> RejectedGestures { get; } = [];

        public List<GlobalHotkeyGesture> RegisterAttempts { get; } = [];

        public GlobalHotkeyGesture? RegisteredGesture { get; private set; }

        public int LastError { get; private set; }

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
