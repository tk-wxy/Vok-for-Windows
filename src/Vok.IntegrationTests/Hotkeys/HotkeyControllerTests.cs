using Vok.Core.Appearance;
using Vok.Core.Platform;
using Vok.Hotkeys;
using Xunit;

namespace Vok.IntegrationTests.Hotkeys;

public sealed class HotkeyControllerTests
{
    private static readonly GlobalHotkeyGesture SavedGesture = new(
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift,
        0x4B);
    private static readonly GlobalHotkeyGesture ReplacementGesture = new(
        GlobalHotkeyModifiers.Alt | GlobalHotkeyModifiers.Shift,
        0x4C);

    [Fact]
    public async Task InitializeConfiguresPersistedGestureBeforeRegistrationStarts()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.Dark, SavedGesture));
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);

        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SavedGesture, controller.CurrentGesture);
        Assert.Equal(SavedGesture, service.CurrentGesture);
    }

    [Fact]
    public async Task CompletedCaptureRegistersAndPersistsCandidate()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.System, SavedGesture));
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await controller.BeginCaptureAsync(TestContext.Current.CancellationToken);
        Assert.True(service.IsSuspended);
        await controller.CompleteCaptureAsync(
            ReplacementGesture,
            TestContext.Current.CancellationToken);

        Assert.False(controller.IsCapturing);
        Assert.False(service.IsSuspended);
        Assert.Equal(ReplacementGesture, controller.CurrentGesture);
        Assert.Equal([ReplacementGesture], store.SavedGestures);
    }

    [Fact]
    public async Task ConflictRestoresPreviousGestureWithoutPersistingCandidate()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.System, SavedGesture));
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.BeginCaptureAsync(TestContext.Current.CancellationToken);
        service.RejectedGesture = ReplacementGesture;

        await Assert.ThrowsAsync<GlobalHotkeyRegistrationException>(
            () => controller.CompleteCaptureAsync(
                ReplacementGesture,
                TestContext.Current.CancellationToken));

        Assert.Equal(SavedGesture, controller.CurrentGesture);
        Assert.Equal(SavedGesture, service.CurrentGesture);
        Assert.False(service.IsSuspended);
        Assert.Empty(store.SavedGestures);
    }

    [Fact]
    public async Task FailureBeforeReplacementRegistrationStillResumesPreviousGesture()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.System, SavedGesture));
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.BeginCaptureAsync(TestContext.Current.CancellationToken);
        service.ChangeFailure = new InvalidOperationException("command unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.CompleteCaptureAsync(
                ReplacementGesture,
                TestContext.Current.CancellationToken));

        Assert.False(controller.IsCapturing);
        Assert.False(service.IsSuspended);
        Assert.Equal(1, service.ResumeCount);
        Assert.Equal(SavedGesture, controller.CurrentGesture);
    }

    [Fact]
    public async Task PersistenceFailureRollsRuntimeRegistrationBack()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.System, SavedGesture))
        {
            SaveFailure = new InvalidOperationException("disk unavailable"),
        };
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.BeginCaptureAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.CompleteCaptureAsync(
                ReplacementGesture,
                TestContext.Current.CancellationToken));

        Assert.Equal(SavedGesture, controller.CurrentGesture);
        Assert.Equal(SavedGesture, service.CurrentGesture);
        Assert.Equal(
            [SavedGesture, ReplacementGesture, SavedGesture],
            service.ChangeAttempts);
    }

    [Fact]
    public async Task CancelledCaptureResumesPreviousGesture()
    {
        var store = new FakeSettingsStore(
            new AppSettings(AppThemeMode.System, SavedGesture));
        var service = new FakeHotkeyService();
        var controller = new HotkeyController(store, service);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);
        await controller.BeginCaptureAsync(TestContext.Current.CancellationToken);

        await controller.CancelCaptureAsync(TestContext.Current.CancellationToken);

        Assert.False(controller.IsCapturing);
        Assert.False(service.IsSuspended);
        Assert.Equal(SavedGesture, service.CurrentGesture);
        Assert.Empty(store.SavedGestures);
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IAppSettingsStore
    {
        public Exception? SaveFailure { get; init; }

        public List<GlobalHotkeyGesture> SavedGestures { get; } = [];

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveThemeModeAsync(
            AppThemeMode themeMode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveHotkeyGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default)
        {
            SavedGestures.Add(gesture);
            return SaveFailure is null
                ? Task.CompletedTask
                : Task.FromException(SaveFailure);
        }
    }

    private sealed class FakeHotkeyService : IConfigurableGlobalHotkeyService
    {
        public event EventHandler<HotkeyPressEvent>? PressChanged;
        public event EventHandler<Exception>? Faulted;

        public GlobalHotkeyGesture CurrentGesture { get; private set; } =
            GlobalHotkeyGesture.Default;

        public string DisplayGesture => CurrentGesture.DisplayName;

        public bool IsSuspended { get; private set; }

        public GlobalHotkeyGesture? RejectedGesture { get; set; }

        public Exception? ChangeFailure { get; set; }

        public int ResumeCount { get; private set; }

        public List<GlobalHotkeyGesture> ChangeAttempts { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SuspendAsync(CancellationToken cancellationToken = default)
        {
            IsSuspended = true;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            ResumeCount++;
            IsSuspended = false;
            return Task.CompletedTask;
        }

        public Task ChangeGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default)
        {
            ChangeAttempts.Add(gesture);
            if (ChangeFailure is not null)
            {
                return Task.FromException(ChangeFailure);
            }

            IsSuspended = false;
            if (gesture == RejectedGesture)
            {
                return Task.FromException(
                    new GlobalHotkeyRegistrationException(gesture, 1409));
            }

            CurrentGesture = gesture;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _ = PressChanged;
            _ = Faulted;
        }
    }
}
