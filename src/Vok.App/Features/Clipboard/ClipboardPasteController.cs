using Vok.Core.Clipboard;
using Vok.Hosting;

namespace Vok.Features.Clipboard;

public sealed class ClipboardPasteController : IDisposable
{
    private readonly ClipboardViewModel _viewModel;
    private readonly IClipboardHistoryRepository _repository;
    private readonly IClipboardPlatformService _platform;
    private readonly IWorkspaceCommands _workspace;
    private readonly IPasteInputService _input;
    private readonly ClipboardPasteStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ClipboardPasteController(
        ClipboardViewModel viewModel,
        IClipboardHistoryRepository repository,
        IClipboardPlatformService platform,
        IWorkspaceCommands workspace,
        IPasteInputService input)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _viewModel.PasteActionAsync = PasteAsync;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_viewModel.PasteActionAsync == PasteAsync)
        {
            _viewModel.PasteActionAsync = null;
        }

    }

    private async Task PasteAsync(ClipboardItemViewModel item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cancellationToken = _lifetime.Token;
        if (!await _actionGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        var transition = _stateMachine.Begin(item.Id);
        if (!transition.Accepted)
        {
            _actionGate.Release();
            return;
        }

        var generation = transition.Generation;
        ClipboardWriteReceipt? receipt = null;
        try
        {
            var payload = await _repository
                .GetReplayPayloadAsync(item.Id, cancellationToken)
                .ConfigureAwait(true);
            if (payload is null)
            {
                Finish(generation, ClipboardPasteState.Failed, "此条历史已不存在或内容不可用。");
                return;
            }

            RequireAdvance(
                generation,
                ClipboardPasteState.Materializing,
                ClipboardPasteState.WritingClipboard);
            receipt = await _platform
                .WriteAsync(payload, Guid.NewGuid(), cancellationToken)
                .ConfigureAwait(true);

            RequireAdvance(
                generation,
                ClipboardPasteState.WritingClipboard,
                ClipboardPasteState.HidingWorkspace);
            var hide = await _workspace
                .HideForPasteActionAsync(cancellationToken)
                .ConfigureAwait(true);
            if (!hide.Hidden || !hide.CanInjectInput)
            {
                Finish(
                    generation,
                    ClipboardPasteState.CopiedOnly,
                    "已复制到系统剪贴板；目标窗口未确认恢复，请手动粘贴。");
                return;
            }

            RequireAdvance(
                generation,
                ClipboardPasteState.HidingWorkspace,
                ClipboardPasteState.VerifyingCurrent);
            if (!await _platform
                    .IsCurrentAsync(receipt.Value, cancellationToken)
                    .ConfigureAwait(true))
            {
                Finish(
                    generation,
                    ClipboardPasteState.Failed,
                    "剪贴板已被其他程序更新，未自动粘贴。");
                return;
            }

            RequireAdvance(
                generation,
                ClipboardPasteState.VerifyingCurrent,
                ClipboardPasteState.InjectingPaste);
            if (!await _input
                    .InjectPasteAsync(hide.Target, cancellationToken)
                    .ConfigureAwait(true))
            {
                Finish(
                    generation,
                    ClipboardPasteState.CopiedOnly,
                    "已复制到系统剪贴板；目标输入焦点发生变化，请手动粘贴。");
                return;
            }

            Finish(generation, ClipboardPasteState.Completed, "已粘贴到呼出前目标。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _stateMachine.Cancel(generation);
        }
        catch (Exception exception)
        {
            Finish(
                generation,
                receipt is null ? ClipboardPasteState.Failed : ClipboardPasteState.CopiedOnly,
                receipt is null
                    ? $"无法写入剪贴板：{exception.Message}"
                    : $"内容已复制，但自动粘贴未完成：{exception.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private void RequireAdvance(
        long generation,
        ClipboardPasteState expected,
        ClipboardPasteState next)
    {
        if (!_stateMachine.Advance(generation, expected, next))
        {
            throw new InvalidOperationException("剪贴板粘贴动作已被较新的代次替换。");
        }
    }

    private void Finish(
        long generation,
        ClipboardPasteState terminal,
        string status)
    {
        if (_stateMachine.Finish(generation, terminal))
        {
            _viewModel.SetActionStatus(status);
        }
    }
}
