using Vok.Core.Launcher;
using Vok.Hosting;

namespace Vok.Features.Launcher;

public sealed class LauncherActionController : IDisposable
{
    private readonly LauncherViewModel _viewModel;
    private readonly IWorkspaceCommands _workspace;
    private readonly ILauncherShellService _shell;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public LauncherActionController(
        LauncherViewModel viewModel,
        IWorkspaceCommands workspace,
        ILauncherShellService shell)
    {
        _viewModel = viewModel;
        _workspace = workspace;
        _shell = shell;
        _viewModel.OpenActionAsync = OpenAsync;
        _viewModel.RevealActionAsync = RevealAsync;
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

    }

    private Task OpenAsync(LauncherItemViewModel item) =>
        RunAsync(item, _shell.OpenAsync);

    private Task RevealAsync(LauncherItemViewModel item) =>
        RunAsync(item, _shell.RevealInExplorerAsync);

    private async Task RunAsync(
        LauncherItemViewModel item,
        Func<LauncherItem, CancellationToken, Task> shellAction)
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
                    .HideForLauncherActionAsync(cancellationToken)
                    .ConfigureAwait(true))
            {
                throw new InvalidOperationException("工作台未能完成隐藏事务，已取消启动操作。");
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
