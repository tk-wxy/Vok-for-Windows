using Vok.Core.Platform;
using Xunit;

namespace Vok.Tests.Platform;

public sealed class HotkeyPressSemanticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(249)]
    [InlineData(250)]
    public void ShortPressFromHiddenKeepsWorkspaceVisible(int heldMilliseconds)
    {
        var action = HotkeyPressSemantics.GetReleaseAction(
            TimeSpan.FromMilliseconds(heldMilliseconds),
            workspaceWasOpenAtPress: false);

        Assert.Equal(HotkeyReleaseAction.KeepVisible, action);
    }

    [Theory]
    [InlineData(251)]
    [InlineData(5000)]
    public void LongPressFromHiddenHidesWorkspace(int heldMilliseconds)
    {
        var action = HotkeyPressSemantics.GetReleaseAction(
            TimeSpan.FromMilliseconds(heldMilliseconds),
            workspaceWasOpenAtPress: false);

        Assert.Equal(HotkeyReleaseAction.Hide, action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(251)]
    public void AnyCompletedPressFromOpenWorkspaceHides(int heldMilliseconds)
    {
        var action = HotkeyPressSemantics.GetReleaseAction(
            TimeSpan.FromMilliseconds(heldMilliseconds),
            workspaceWasOpenAtPress: true);

        Assert.Equal(HotkeyReleaseAction.Hide, action);
    }

    [Fact]
    public void NegativeDurationIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HotkeyPressSemantics.GetReleaseAction(
                TimeSpan.FromMilliseconds(-1),
                workspaceWasOpenAtPress: false));
    }
}
