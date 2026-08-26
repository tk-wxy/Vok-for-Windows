using Vok.Core.Appearance;

namespace Vok.Appearance;

public sealed class AppearanceController
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly IAppThemeApplier _themeApplier;
    private AppThemeMode _persistedThemeMode = AppThemeMode.System;
    private long _changeGeneration;
    private bool _initialized;

    public AppearanceController(
        IAppSettingsStore settingsStore,
        IAppThemeApplier themeApplier)
    {
        _settingsStore = settingsStore;
        _themeApplier = themeApplier;
    }

    public AppThemeMode SelectedThemeMode { get; private set; } = AppThemeMode.System;

    public Exception? InitializationError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        AppSettings settings;
        try
        {
            settings = await _settingsStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            InitializationError = exception;
            settings = AppSettings.Default;
        }

        _themeApplier.Apply(settings.ThemeMode);
        SelectedThemeMode = settings.ThemeMode;
        _persistedThemeMode = settings.ThemeMode;
        _initialized = true;
    }

    public async Task ChangeThemeAsync(
        AppThemeMode themeMode,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Appearance must be initialized before changing the theme.");
        }

        if (!Enum.IsDefined(themeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(themeMode), themeMode, null);
        }

        if (themeMode == SelectedThemeMode)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _changeGeneration);
        _themeApplier.Apply(themeMode);
        SelectedThemeMode = themeMode;

        try
        {
            await _settingsStore
                .SaveThemeModeAsync(themeMode, cancellationToken)
                .ConfigureAwait(true);
            _persistedThemeMode = themeMode;
        }
        catch
        {
            if (generation == Volatile.Read(ref _changeGeneration))
            {
                _themeApplier.Apply(_persistedThemeMode);
                SelectedThemeMode = _persistedThemeMode;
            }

            throw;
        }
    }
}
