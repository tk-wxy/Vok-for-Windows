using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Input;
using Xunit;

namespace Vok.IntegrationTests.Input;

public sealed class Win32PasteInputServiceTests
{
    [Fact]
    public void ExactForegroundAndFocusedChildMatchIsAccepted()
    {
        var expected = new ForegroundTarget(100, 10, 11, 101, 10, 11);

        Assert.True(Win32PasteInputService.MatchesExpectedTarget(expected, expected));
    }

    [Theory]
    [InlineData(200, 10, 11, 101, 10, 11)]
    [InlineData(100, 20, 11, 101, 10, 11)]
    [InlineData(100, 10, 21, 101, 10, 11)]
    [InlineData(100, 10, 11, 201, 10, 11)]
    [InlineData(100, 10, 11, 101, 20, 11)]
    [InlineData(100, 10, 11, 101, 10, 21)]
    public void AnyTargetIdentityChangeIsRejected(
        long window,
        uint process,
        uint thread,
        long focus,
        uint focusProcess,
        uint focusThread)
    {
        var expected = new ForegroundTarget(100, 10, 11, 101, 10, 11);
        var observed = new ForegroundTarget(
            (nint)window,
            process,
            thread,
            (nint)focus,
            focusProcess,
            focusThread);

        Assert.False(Win32PasteInputService.MatchesExpectedTarget(expected, observed));
    }
}
