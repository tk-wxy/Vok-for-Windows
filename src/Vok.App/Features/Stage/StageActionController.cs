using Vok.Core.Stage;
using Vok.Hosting;

namespace Vok.Features.Stage;

public sealed class StageActionController : IDisposable
{
    private readonly StageViewModel _viewModel;
    private readonly IWorkspaceCommands _workspace;
    private readonly IStageFileService _files;
    private readonly IStageClipboardService _clipboard;
    private readonly IStageRelinkTargetPicker _relinkTargetPicker;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public StageActionController(
        StageViewModel viewModel,
        IWorkspaceCommands workspace,
        IStageFileService files,
        IStageClipboardService clipboard,
        IStageRelinkTargetPicker relinkTargetPicker)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _relinkTargetPicker = relinkTargetPicker ??
            throw new ArgumentNullException(nameof(relinkTargetPicker));
        _viewModel.OpenActionAsync = OpenAsync;
        _viewModel.RevealActionAsync = RevealAsync;
        _viewModel.CopyPathActionAsync = CopyPathAsync;
        _viewModel.RelinkTargetPickerAsync = SelectRelinkTargetAsync;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_viewModel.OpenActionAsync == OpenAsync)
        {
            _viewModel.OpenActionAsync = null;
        }

        if (_viewModel.RevealActionAsync == RevealAsync)
        {
            _viewModel.RevealActionAsync = null;
        }

        if (_viewModel.CopyPathActionAsync == CopyPathAsync)
        {
            _viewModel.CopyPathActionAsync = null;
        }

        if (_viewModel.RelinkTargetPickerAsync == SelectRelinkTargetAsync)
        {
            _viewModel.RelinkTargetPickerAsync = null;
        }
    }

    private Task OpenAsync(StageItemViewModel item) =>
        RunAsync(item, _files.OpenAsync);

    private Task RevealAsync(StageItemViewModel item) =>
        RunAsync(item, _files.RevealInExplorerAsync);

    private async Task CopyPathAsync(StageItemViewModel item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cancellationToken = _lifetime.Token;
        if (!await _actionGate
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _clipboard
                .CopyPathTextAsync(item.TargetValue, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private Task<StageRelinkSelection> SelectRelinkTargetAsync(
        StageItemViewModel item,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var selection = _relinkTargetPicker.SelectTarget(item.Item, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(selection);
    }

    private async Task RunAsync(
        StageItemViewModel item,
        Func<StageItem, CancellationToken, Task> shellAction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cancellationToken = _lifetime.Token;
        if (!await _actionGate
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (!await _workspace
                    .HideForStageManagementActionAsync(cancellationToken)
                    .ConfigureAwait(true))
            {
                throw new InvalidOperationException(
                    "工作台未能完成隐藏事务，已取消中转站文件操作。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await shellAction(item.Item, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _actionGate.Release();
        }
    }
}
