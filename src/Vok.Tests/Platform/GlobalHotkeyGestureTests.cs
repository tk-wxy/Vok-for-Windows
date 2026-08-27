using Vok.Core.Platform;
using Xunit;

namespace Vok.Tests.Platform;

public sealed class GlobalHotkeyGestureTests
{
    [Fact]
    public void DefaultGestureHasStableDisplayName()
    {
        Assert.Equal("Ctrl + Alt + Space", GlobalHotkeyGesture.Default.DisplayName);
    }

    [Theory]
    [InlineData(GlobalHotkeyModifiers.None, 0x41)]
    [InlineData(GlobalHotkeyModifiers.Control, 0x11)]
    [InlineData(GlobalHotkeyModifiers.Control, 0x7B)]
    [InlineData((GlobalHotkeyModifiers)16, 0x41)]
    [InlineData(GlobalHotkeyModifiers.Control, 0)]
    public void UnsupportedGestureIsRejected(
        GlobalHotkeyModifiers modifiers,
        uint virtualKey)
    {
        Assert.False(GlobalHotkeyGesture.TryCreate(modifiers, virtualKey, out _));
    }

    [Fact]
    public void GestureFormatsModifiersAndFunctionKey()
    {
        var gesture = new GlobalHotkeyGesture(
            GlobalHotkeyModifiers.Control |
            GlobalHotkeyModifiers.Shift |
            GlobalHotkeyModifiers.Windows,
            0x76);

        Assert.Equal("Ctrl + Shift + Win + F7", gesture.DisplayName);
    }
}
