using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Platform;
using Xunit;

namespace Vok.IntegrationTests.Appearance;

public sealed class AppearanceControllerTests
{
    [Fact]
    public async Task InitializeLoadsAndAppliesPersistedTheme()
    {
        var store = new FakeSettingsStore(new AppSettings(AppThemeMode.Dark));
        var applier = new RecordingThemeApplier();
        var controller = new AppearanceController(store, applier);

        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppThemeMode.Dark, controller.SelectedThemeMode);
        Assert.Equal([AppThemeMode.Dark], applier.AppliedModes);
        Assert.Null(controller.InitializationError);
    }

    [Fact]
    public async Task InitializeFallsBackToSystemWhenSettingsCannotBeRead()
    {
        var failure = new InvalidOperationException("load");
        var store = new FakeSettingsStore(AppSettings.Default)
        {
            LoadFailure = failure,
        };
        var applier = new RecordingThemeApplier();
        var controller = new AppearanceController(store, applier);

        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AppThemeMode.System, controller.SelectedThemeMode);
        Assert.Equal([AppThemeMode.System], applier.AppliedModes);
        Assert.Same(failure, controller.InitializationError);
    }

    [Fact]
    public async Task SaveFailureRollsBackAppliedAndSelectedTheme()
    {
        var store = new FakeSettingsStore(new AppSettings(AppThemeMode.Light))
        {
            SaveFailure = new InvalidOperationException("save"),
        };
        var applier = new RecordingThemeApplier();
        var controller = new AppearanceController(store, applier);
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ChangeThemeAsync(
                AppThemeMode.Dark,
                TestContext.Current.CancellationToken));

        Assert.Equal(AppThemeMode.Light, controller.SelectedThemeMode);
        Assert.Equal(
            [AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Light],
            applier.AppliedModes);
    }

    [Fact]
    public async Task SelectingCurrentThemeDoesNotWriteSettingsAgain()
    {
        var store = new FakeSettingsStore(new AppSettings(AppThemeMode.Light));
        var controller = new AppearanceController(store, new RecordingThemeApplier());
        await controller.InitializeAsync(TestContext.Current.CancellationToken);

        await controller.ChangeThemeAsync(
            AppThemeMode.Light,
            TestContext.Current.CancellationToken);

        Assert.Empty(store.SavedModes);
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IAppSettingsStore
    {
        public Exception? LoadFailure { get; init; }

        public Exception? SaveFailure { get; init; }

        public List<AppThemeMode> SavedModes { get; } = [];

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            LoadFailure is null
                ? Task.FromResult(settings)
                : Task.FromException<AppSettings>(LoadFailure);

        public Task SaveThemeModeAsync(
            AppThemeMode themeMode,
            CancellationToken cancellationToken = default)
        {
            SavedModes.Add(themeMode);
            return SaveFailure is null
                ? Task.CompletedTask
                : Task.FromException(SaveFailure);
        }

        public Task SaveHotkeyGestureAsync(
            GlobalHotkeyGesture gesture,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingThemeApplier : IAppThemeApplier
    {
        public List<AppThemeMode> AppliedModes { get; } = [];

        public void Apply(AppThemeMode themeMode) => AppliedModes.Add(themeMode);
    }
}
