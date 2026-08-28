using System.Runtime.InteropServices;
using Vok.Core.Clipboard;
using Vok.Core.Platform;
using Vok.Core.Workspace;

namespace Vok.Infrastructure.Windows.Input;

public sealed partial class Win32PasteInputService(
    IForegroundWindowService foregroundWindowService) : IPasteInputService
{
    private const uint KeyboardInputType = 1;
    private const uint KeyUpFlag = 0x0002;
    private const ushort ControlVirtualKey = 0x11;
    private const ushort VVirtualKey = 0x56;

    private readonly IForegroundWindowService _foregroundWindowService =
        foregroundWindowService ?? throw new ArgumentNullException(nameof(foregroundWindowService));

    public Task<bool> InjectPasteAsync(
        ForegroundTarget expectedTarget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesExpectedTarget(
                expectedTarget,
                _foregroundWindowService.Capture()))
        {
            return Task.FromResult(false);
        }

        var inputs = new[]
        {
            Key(ControlVirtualKey, keyUp: false),
            Key(VVirtualKey, keyUp: false),
            Key(VVirtualKey, keyUp: true),
            Key(ControlVirtualKey, keyUp: true),
        };
        var inserted = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeInput>());
        if (inserted == inputs.Length)
        {
            return Task.FromResult(true);
        }

        var releases = new[]
        {
            Key(VVirtualKey, keyUp: true),
            Key(ControlVirtualKey, keyUp: true),
        };
        _ = SendInput(
            (uint)releases.Length,
            releases,
            Marshal.SizeOf<NativeInput>());
        return Task.FromResult(false);
    }

    internal static bool MatchesExpectedTarget(
        ForegroundTarget expected,
        ForegroundTarget observed)
    {
        if (!expected.CanRestore ||
            observed.WindowHandle != expected.WindowHandle ||
            observed.ProcessId != expected.ProcessId ||
            observed.ThreadId != expected.ThreadId)
        {
            return false;
        }

        return expected.FocusedWindowHandle == 0 ||
            observed.FocusedWindowHandle == expected.FocusedWindowHandle &&
            observed.FocusedProcessId == expected.FocusedProcessId &&
            observed.FocusedThreadId == expected.FocusedThreadId;
    }

    private static NativeInput Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = KeyboardInputType,
        Data = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyUpFlag : 0,
            },
        },
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeHardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
