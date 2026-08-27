using Vok.Core.Appearance;
using Vok.Core.Platform;

namespace Vok.Hotkeys;

public sealed class HotkeyController
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly IConfigurableGlobalHotkeyService _hotkeyService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _isCapturing;

    public HotkeyController(
        IAppSettingsStore settingsStore,
        IConfigurableGlobalHotkeyService hotkeyService)
    {
        _settingsStore = settingsStore;
        _hotkeyService = hotkeyService;
    }

    public GlobalHotkeyGesture CurrentGesture { get; private set; } =
        GlobalHotkeyGesture.Default;

    public Exception? InitializationError { get; private set; }

    public bool IsCapturing => _isCapturing;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                InitializationError = exception;
                settings = AppSettings.Default;
            }

            await _hotkeyService
                .ChangeGestureAsync(settings.HotkeyGesture, cancellationToken)
                .ConfigureAwait(false);
            CurrentGesture = _hotkeyService.CurrentGesture;
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task BeginCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (_isCapturing)
            {
                return;
            }

            await _hotkeyService
                .SuspendAsync(cancellationToken)
                .ConfigureAwait(false);
            _isCapturing = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteCaptureAsync(
        GlobalHotkeyGesture gesture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_isCapturing)
            {
                throw new InvalidOperationException("No global hotkey capture is active.");
            }

            var previous = CurrentGesture;
            try
            {
                await _hotkeyService
                    .ChangeGestureAsync(gesture, CancellationToken.None)
                    .ConfigureAwait(false);
                _isCapturing = false;

                if (gesture == previous)
                {
                    CurrentGesture = _hotkeyService.CurrentGesture;
                    return;
                }

                try
                {
                    await _settingsStore
                        .SaveHotkeyGestureAsync(gesture, CancellationToken.None)
                        .ConfigureAwait(false);
                    CurrentGesture = gesture;
                }
                catch (Exception saveFailure)
                {
                    try
                    {
                        await _hotkeyService
                            .ChangeGestureAsync(previous, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception rollbackFailure)
                    {
                        CurrentGesture = _hotkeyService.CurrentGesture;
                        throw new AggregateException(
                            "The hotkey setting could not be saved and the runtime registration could not be rolled back.",
                            saveFailure,
                            rollbackFailure);
                    }

                    CurrentGesture = previous;
                    throw;
                }
            }
            catch (Exception captureFailure)
            {
                _isCapturing = false;
                CurrentGesture = _hotkeyService.CurrentGesture;
                try
                {
                    await _hotkeyService
                        .ResumeAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        "The hotkey capture failed and the previous registration could not be resumed.",
                        captureFailure,
                        restoreFailure);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_isCapturing)
            {
                return;
            }

            try
            {
                await _hotkeyService
                    .ResumeAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                _isCapturing = false;
                CurrentGesture = _hotkeyService.CurrentGesture;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Hotkey settings must be initialized before use.");
        }
    }
}
