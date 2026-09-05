using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vok.Core.Stage;
using Vok.Features.Stage;
using Vok.IntegrationTests.Infrastructure;
using Xunit;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed partial class StageRelinkTargetPickerTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void RealOwnedDialogsCancelWithoutDeactivatingOrHidingTheirOwner()
    {
        wpf.Invoke(() =>
        {
            var owner = new Window
            {
                ShowInTaskbar = false,
                Topmost = true,
                Width = 320,
                Height = 200,
                Title = "Vok Stage relink owner gate",
            };
            var deactivatedCount = 0;
            EventHandler deactivated = (_, _) => deactivatedCount++;
            wpf.Application.Deactivated += deactivated;
            owner.Show();
            _ = owner.Activate();
            try
            {
                var factory = new WpfStageRelinkDialogFactory();
                RunRealDialogCancellationGate(
                    factory,
                    owner,
                    StageItemKind.File,
                    "Vok Stage file relink automated gate");
                RunRealDialogCancellationGate(
                    factory,
                    owner,
                    StageItemKind.Directory,
                    "Vok Stage folder relink automated gate");

                Assert.True(owner.IsVisible);
                Assert.True(owner.Topmost);
                Assert.Equal(0, deactivatedCount);
            }
            finally
            {
                owner.Close();
                wpf.Application.Deactivated -= deactivated;
            }
        });
    }

    [Fact]
    public void PickerBuildsKindSpecificOwnedSingleTargetRequests()
    {
        wpf.Invoke(() =>
        {
            var window = new Window
            {
                ShowInTaskbar = false,
                Width = 320,
                Height = 200,
            };
            window.Show();
            try
            {
                AssertRequest(
                    window,
                    CreateItem(StageItemKind.File, @"C:\Stage\Document.txt", "Document.txt"),
                    expectedTitlePrefix: "重新关联文件",
                    expectedInitialDirectory: @"C:\Stage",
                    expectedSuggestedName: "Document.txt",
                    expectedFilter: "所有文件 (*.*)|*.*");
                AssertRequest(
                    window,
                    CreateItem(StageItemKind.Directory, @"C:\Stage\Folder", "Folder"),
                    expectedTitlePrefix: "重新关联文件夹",
                    expectedInitialDirectory: @"C:\Stage\Folder",
                    expectedSuggestedName: "Folder",
                    expectedFilter: null);
                AssertRequest(
                    window,
                    CreateItem(StageItemKind.ShellLink, @"C:\Stage\Target.lnk", "Target.lnk"),
                    expectedTitlePrefix: "重新关联快捷方式",
                    expectedInitialDirectory: @"C:\Stage",
                    expectedSuggestedName: "Target.lnk",
                    expectedFilter: "Windows 快捷方式 (*.lnk)|*.lnk");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CancellationAndHiddenOwnerNeverPublishAPath()
    {
        wpf.Invoke(() =>
        {
            var item = CreateItem(
                StageItemKind.File,
                @"C:\Stage\Document.txt",
                "Document.txt");
            var window = new Window
            {
                ShowInTaskbar = false,
                Width = 320,
                Height = 200,
            };
            window.Show();
            try
            {
                var cancelledDialog = new FakeDialog(
                    accepted: false,
                    selectedPath: @"C:\Stage\Replacement.txt");
                var cancelledFactory = new CapturingDialogFactory(cancelledDialog);
                var picker = new WpfStageRelinkTargetPicker(
                    cancelledFactory,
                    () => window);

                Assert.Equal(
                    StageRelinkSelection.Cancelled,
                    picker.SelectTarget(item));
                Assert.Same(window, cancelledDialog.Owner);

                window.Hide();
                var hiddenFactory = new CapturingDialogFactory(
                    new FakeDialog(true, @"C:\Stage\Replacement.txt"));
                var hiddenPicker = new WpfStageRelinkTargetPicker(
                    hiddenFactory,
                    () => window);
                Assert.Equal(
                    StageRelinkSelection.Cancelled,
                    hiddenPicker.SelectTarget(item));
                Assert.Empty(hiddenFactory.Requests);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OwnerHiddenWhileDialogIsOpenCancelsTheReturnedSelection()
    {
        wpf.Invoke(() =>
        {
            var item = CreateItem(
                StageItemKind.File,
                @"C:\Stage\Document.txt",
                "Document.txt");
            var window = new Window
            {
                ShowInTaskbar = false,
                Width = 320,
                Height = 200,
            };
            window.Show();
            try
            {
                var dialog = new FakeDialog(
                    accepted: true,
                    selectedPath: @"C:\Stage\Replacement.txt",
                    onShow: owner => owner.Hide());
                var picker = new WpfStageRelinkTargetPicker(
                    new CapturingDialogFactory(dialog),
                    () => window);

                Assert.Equal(
                    StageRelinkSelection.Cancelled,
                    picker.SelectTarget(item));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertRequest(
        Window owner,
        StageItem item,
        string expectedTitlePrefix,
        string expectedInitialDirectory,
        string expectedSuggestedName,
        string? expectedFilter)
    {
        const string selectedPath = @"C:\Stage\Selected";
        var dialog = new FakeDialog(accepted: true, selectedPath);
        var factory = new CapturingDialogFactory(dialog);
        var picker = new WpfStageRelinkTargetPicker(factory, () => owner);

        var selection = picker.SelectTarget(item);

        Assert.Equal(StageRelinkSelection.Selected(selectedPath), selection);
        Assert.Same(owner, dialog.Owner);
        var request = Assert.Single(factory.Requests);
        Assert.Equal(item.Kind, request.Kind);
        Assert.StartsWith(expectedTitlePrefix, request.Title, StringComparison.Ordinal);
        Assert.Equal(expectedInitialDirectory, request.InitialDirectory);
        Assert.Equal(expectedSuggestedName, request.SuggestedName);
        Assert.Equal(expectedFilter, request.Filter);
        Assert.False(request.DereferenceLinks);
    }

    private static void RunRealDialogCancellationGate(
        IStageRelinkDialogFactory factory,
        Window owner,
        StageItemKind kind,
        string title)
    {
        var request = new StageRelinkDialogRequest(
            kind,
            title,
            Path.GetTempPath(),
            SuggestedName: null,
            kind is StageItemKind.File ? "所有文件 (*.*)|*.*" : null,
            DereferenceLinks: false);
        var dialog = factory.Create(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
        var closeOperation = CloseNativeDialogAsync(
            title,
            timeout.Token);

        var accepted = dialog.ShowDialog(owner);
        var closedWindow = closeOperation.GetAwaiter().GetResult();

        Assert.NotEqual(true, accepted);
        Assert.NotEqual(0, closedWindow.Handle);
        Assert.Equal(ownerHandle, closedWindow.Owner);
        Assert.True(owner.IsVisible);
        Assert.Equal(0, FindNativeDialog(title));
    }

    private static Task<ClosedNativeDialog> CloseNativeDialogAsync(
        string title,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handle = FindNativeDialog(title);
                if (handle != 0)
                {
                    _ = NativeMethods.GetWindowThreadProcessId(
                        handle,
                        out var processId);
                    if (processId == Environment.ProcessId)
                    {
                        var owner = NativeMethods.GetWindow(handle, GetWindowOwner);
                        if (!NativeMethods.PostMessage(
                                handle,
                                WindowMessageClose,
                                0,
                                0))
                        {
                            throw new InvalidOperationException(
                                $"Unable to close Stage relink dialog 0x{handle:X}.");
                        }

                        return new ClosedNativeDialog(handle, owner);
                    }
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

    private static nint FindNativeDialog(string title) =>
        NativeMethods.FindWindowEx(0, 0, "#32770", title);

    private static StageItem CreateItem(
        StageItemKind kind,
        string path,
        string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new StageItem(
            Guid.NewGuid(),
            kind,
            path,
            path.ToUpperInvariant(),
            name,
            1,
            false,
            1,
            now,
            now);
    }

    private sealed class CapturingDialogFactory(IStageRelinkDialog dialog)
        : IStageRelinkDialogFactory
    {
        public List<StageRelinkDialogRequest> Requests { get; } = [];

        public IStageRelinkDialog Create(StageRelinkDialogRequest request)
        {
            Requests.Add(request);
            return dialog;
        }
    }

    private sealed class FakeDialog(
        bool accepted,
        string? selectedPath,
        Action<Window>? onShow = null) : IStageRelinkDialog
    {
        public string? SelectedPath { get; } = selectedPath;

        public Window? Owner { get; private set; }

        public bool? ShowDialog(Window owner)
        {
            Owner = owner;
            onShow?.Invoke(owner);
            return accepted;
        }
    }

    private readonly record struct ClosedNativeDialog(nint Handle, nint Owner);

    private const uint WindowMessageClose = 0x0010;
    private const uint GetWindowOwner = 4;

    private static partial class NativeMethods
    {
        [LibraryImport(
            "user32.dll",
            EntryPoint = "FindWindowExW",
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint FindWindowEx(
            nint parent,
            nint childAfter,
            string? className,
            string? windowName);

        [LibraryImport("user32.dll", EntryPoint = "GetWindow")]
        internal static partial nint GetWindow(nint window, uint command);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        internal static partial uint GetWindowThreadProcessId(
            nint window,
            out int processId);

        [LibraryImport(
            "user32.dll",
            EntryPoint = "PostMessageW",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(
            nint window,
            uint message,
            nint wParam,
            nint lParam);
    }
}
