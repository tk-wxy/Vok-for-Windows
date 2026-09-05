using System.IO;
using System.Windows;
using Microsoft.Win32;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

internal sealed class WpfStageRelinkTargetPicker(
    IStageRelinkDialogFactory dialogFactory,
    Func<Window?> ownerProvider) : IStageRelinkTargetPicker
{
    private readonly IStageRelinkDialogFactory _dialogFactory =
        dialogFactory ?? throw new ArgumentNullException(nameof(dialogFactory));
    private readonly Func<Window?> _ownerProvider =
        ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));

    public StageRelinkSelection SelectTarget(
        StageItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = _ownerProvider();
        if (owner is null || !owner.IsVisible ||
            Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            return StageRelinkSelection.Cancelled;
        }

        var dialog = _dialogFactory.Create(CreateRequest(item));
        var accepted = dialog.ShowDialog(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (accepted != true || !owner.IsVisible ||
            Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            return StageRelinkSelection.Cancelled;
        }

        return StageRelinkSelection.Selected(
            string.IsNullOrWhiteSpace(dialog.SelectedPath)
                ? throw new InvalidOperationException(
                    "The Stage relink dialog accepted without a selected path.")
                : dialog.SelectedPath);
    }

    private static StageRelinkDialogRequest CreateRequest(StageItem item)
    {
        string? initialDirectory;
        string? suggestedName;
        try
        {
            if (item.Kind is StageItemKind.Directory)
            {
                initialDirectory = item.TargetValue;
                suggestedName = item.ResolvedName;
            }
            else
            {
                initialDirectory = Path.GetDirectoryName(item.TargetValue);
                suggestedName = Path.GetFileName(item.TargetValue);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            initialDirectory = null;
            suggestedName = null;
        }

        return item.Kind switch
        {
            StageItemKind.File => new StageRelinkDialogRequest(
                item.Kind,
                $"重新关联文件：{item.ResolvedName}",
                initialDirectory,
                suggestedName,
                "所有文件 (*.*)|*.*",
                DereferenceLinks: false),
            StageItemKind.Directory => new StageRelinkDialogRequest(
                item.Kind,
                $"重新关联文件夹：{item.ResolvedName}",
                initialDirectory,
                suggestedName,
                Filter: null,
                DereferenceLinks: false),
            StageItemKind.ShellLink => new StageRelinkDialogRequest(
                item.Kind,
                $"重新关联快捷方式：{item.ResolvedName}",
                initialDirectory,
                suggestedName,
                "Windows 快捷方式 (*.lnk)|*.lnk",
                DereferenceLinks: false),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null),
        };
    }
}

internal sealed record StageRelinkDialogRequest(
    StageItemKind Kind,
    string Title,
    string? InitialDirectory,
    string? SuggestedName,
    string? Filter,
    bool DereferenceLinks);

internal interface IStageRelinkDialogFactory
{
    IStageRelinkDialog Create(StageRelinkDialogRequest request);
}

internal interface IStageRelinkDialog
{
    string? SelectedPath { get; }

    bool? ShowDialog(Window owner);
}

internal sealed class WpfStageRelinkDialogFactory : IStageRelinkDialogFactory
{
    public IStageRelinkDialog Create(StageRelinkDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            StageItemKind.File or StageItemKind.ShellLink =>
                new OpenFileStageRelinkDialog(request),
            StageItemKind.Directory => new OpenFolderStageRelinkDialog(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, null),
        };
    }

    private sealed class OpenFileStageRelinkDialog
        : IStageRelinkDialog
    {
        private readonly OpenFileDialog _dialog;

        public OpenFileStageRelinkDialog(StageRelinkDialogRequest request)
        {
            _dialog = new OpenFileDialog
            {
                Title = request.Title,
                InitialDirectory = request.InitialDirectory ?? string.Empty,
                FileName = request.SuggestedName ?? string.Empty,
                Filter = request.Filter ?? string.Empty,
                Multiselect = false,
                CheckFileExists = true,
                CheckPathExists = true,
                ValidateNames = true,
                DereferenceLinks = request.DereferenceLinks,
            };
        }

        public string? SelectedPath => _dialog.FileName;

        public bool? ShowDialog(Window owner) => _dialog.ShowDialog(owner);
    }

    private sealed class OpenFolderStageRelinkDialog
        : IStageRelinkDialog
    {
        private readonly OpenFolderDialog _dialog;

        public OpenFolderStageRelinkDialog(StageRelinkDialogRequest request)
        {
            _dialog = new OpenFolderDialog
            {
                Title = request.Title,
                InitialDirectory = request.InitialDirectory ?? string.Empty,
                FolderName = request.SuggestedName ?? string.Empty,
                Multiselect = false,
                ValidateNames = true,
                DereferenceLinks = request.DereferenceLinks,
            };
        }

        public string? SelectedPath => _dialog.FolderName;

        public bool? ShowDialog(Window owner) => _dialog.ShowDialog(owner);
    }
}
