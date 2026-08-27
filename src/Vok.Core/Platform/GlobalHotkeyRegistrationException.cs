namespace Vok.Core.Platform;

public sealed class GlobalHotkeyRegistrationException : Exception
{
    public GlobalHotkeyRegistrationException(
        GlobalHotkeyGesture gesture,
        int nativeErrorCode)
        : base($"Unable to register {gesture.DisplayName}. The gesture may already be in use.")
    {
        Gesture = gesture;
        NativeErrorCode = nativeErrorCode;
    }

    public GlobalHotkeyGesture Gesture { get; }

    public int NativeErrorCode { get; }
}
