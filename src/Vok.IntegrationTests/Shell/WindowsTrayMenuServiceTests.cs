using Vok.Core.Platform;
using Vok.Infrastructure.Windows.Shell;
using Xunit;

namespace Vok.IntegrationTests.Shell;

public sealed class WindowsTrayMenuServiceTests
{
    private static readonly Guid IconId = new("5021d746-ead9-4551-8b04-6d060c19559c");

    [Fact]
    public void NativeMenuResourcesCanBeConstructedAndDestroyed()
    {
        var native = new WindowsTrayMenuNativeMethods();
        var menu = native.CreatePopupMenu();
        try
        {
            native.AppendMenuItem(menu, 1, "显示界面(&S)");
            native.AppendSeparator(menu);
            native.AppendMenuItem(menu, 2, "退出(&X)");
            native.SetDefaultMenuItem(menu, 1);
        }
        finally
        {
            native.DestroyMenu(menu);
        }
    }

    [Fact]
    public void BuildsStandardMenuAndMapsSelectedCommand()
    {
        var native = new FakeNativeMethods
        {
            SelectedCommand = WindowsTrayMenuService.OpenSettingsCommandId,
        };
        var service = new WindowsTrayMenuService(native);

        var command = service.Show((nint)41, IconId, keyboardInvocation: false);

        Assert.Equal(TrayIconCommand.OpenSettings, command);
        Assert.Equal(
            [
                "create",
                "item:1:显示界面(&S)",
                "item:2:设置(&O)",
                "separator",
                "item:3:退出(&X)",
                "default:1",
                "cursor",
                "foreground:41",
                "track:41:80,90",
                "dismissal:41",
                "destroy:71",
            ],
            native.Calls);
    }

    [Fact]
    public void KeyboardInvocationUsesIconAnchorWhenAvailable()
    {
        var native = new FakeNativeMethods
        {
            IconAnchorAvailable = true,
            IconAnchor = new TrayMenuPoint(120, 140),
        };
        var service = new WindowsTrayMenuService(native);

        _ = service.Show((nint)41, IconId, keyboardInvocation: true);

        Assert.Contains("icon-anchor:41", native.Calls);
        Assert.Contains("track:41:120,140", native.Calls);
        Assert.DoesNotContain("cursor", native.Calls);
    }

    [Fact]
    public void DismissalReturnsNoCommandAndStillCompletesNativeCleanup()
    {
        var native = new FakeNativeMethods { SelectedCommand = 0 };
        var service = new WindowsTrayMenuService(native);

        var command = service.Show((nint)41, IconId, keyboardInvocation: false);

        Assert.Null(command);
        Assert.Contains("dismissal:41", native.Calls);
        Assert.Contains("destroy:71", native.Calls);
    }

    [Fact]
    public void BuildFailureDestroysMenuWithoutPostingTrackCompletion()
    {
        var failure = new InvalidOperationException("append");
        var native = new FakeNativeMethods
        {
            AppendFailure = failure,
        };
        var service = new WindowsTrayMenuService(native);

        var actual = Assert.Throws<InvalidOperationException>(
            () => service.Show((nint)41, IconId, keyboardInvocation: false));

        Assert.Same(failure, actual);
        Assert.DoesNotContain("dismissal:41", native.Calls);
        Assert.Contains("destroy:71", native.Calls);
    }

    [Fact]
    public void OperationAndCleanupFailuresAreReportedAfterAllCleanupRuns()
    {
        var native = new FakeNativeMethods
        {
            TrackFailure = new InvalidOperationException("track"),
            DismissalFailure = new InvalidOperationException("dismissal"),
            DestroyFailure = new InvalidOperationException("destroy"),
        };
        var service = new WindowsTrayMenuService(native);

        var failure = Assert.Throws<AggregateException>(
            () => service.Show((nint)41, IconId, keyboardInvocation: false));

        Assert.Equal(3, failure.InnerExceptions.Count);
        Assert.Contains("dismissal:41", native.Calls);
        Assert.Contains("destroy:71", native.Calls);
    }

    private sealed class FakeNativeMethods : IWindowsTrayMenuNativeMethods
    {
        public List<string> Calls { get; } = [];

        public uint SelectedCommand { get; init; }

        public bool IconAnchorAvailable { get; init; }

        public TrayMenuPoint IconAnchor { get; init; }

        public Exception? AppendFailure { get; init; }

        public Exception? TrackFailure { get; init; }

        public Exception? DismissalFailure { get; init; }

        public Exception? DestroyFailure { get; init; }

        public nint CreatePopupMenu()
        {
            Calls.Add("create");
            return (nint)71;
        }

        public void AppendMenuItem(nint menu, uint commandId, string text)
        {
            Calls.Add($"item:{commandId}:{text}");
            if (AppendFailure is not null)
            {
                throw AppendFailure;
            }
        }

        public void AppendSeparator(nint menu) => Calls.Add("separator");

        public void SetDefaultMenuItem(nint menu, uint commandId) =>
            Calls.Add($"default:{commandId}");

        public bool TryGetIconAnchor(
            nint ownerWindow,
            Guid iconId,
            out TrayMenuPoint anchor)
        {
            Calls.Add($"icon-anchor:{ownerWindow}");
            anchor = IconAnchor;
            return IconAnchorAvailable;
        }

        public TrayMenuPoint GetCursorPosition()
        {
            Calls.Add("cursor");
            return new TrayMenuPoint(80, 90);
        }

        public void SetForegroundWindow(nint ownerWindow) =>
            Calls.Add($"foreground:{ownerWindow}");

        public uint TrackPopupMenu(nint menu, nint ownerWindow, TrayMenuPoint anchor)
        {
            Calls.Add($"track:{ownerWindow}:{anchor.X},{anchor.Y}");
            if (TrackFailure is not null)
            {
                throw TrackFailure;
            }

            return SelectedCommand;
        }

        public void PostMenuDismissal(nint ownerWindow)
        {
            Calls.Add($"dismissal:{ownerWindow}");
            if (DismissalFailure is not null)
            {
                throw DismissalFailure;
            }
        }

        public void DestroyMenu(nint menu)
        {
            Calls.Add($"destroy:{menu}");
            if (DestroyFailure is not null)
            {
                throw DestroyFailure;
            }
        }
    }
}
