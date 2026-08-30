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
    private readonly IClipboardFilePresentationService? _filePresentation;
    private readonly IClipboardImageReplayMaterializer? _imageReplayMaterializer;
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
        : this(
            viewModel,
            repository,
            platform,
            workspace,
            input,
            filePresentation: null,
            imageReplayMaterializer: null)
    {
    }

    public ClipboardPasteController(
        ClipboardViewModel viewModel,
        IClipboardHistoryRepository repository,
        IClipboardPlatformService platform,
        IWorkspaceCommands workspace,
        IPasteInputService input,
        IClipboardFilePresentationService? filePresentation)
        : this(
            viewModel,
            repository,
            platform,
            workspace,
            input,
            filePresentation,
            imageReplayMaterializer: null)
    {
    }

    public ClipboardPasteController(
        ClipboardViewModel viewModel,
        IClipboardHistoryRepository repository,
        IClipboardPlatformService platform,
        IWorkspaceCommands workspace,
        IPasteInputService input,
        IClipboardFilePresentationService? filePresentation,
        IClipboardImageReplayMaterializer? imageReplayMaterializer)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _filePresentation = filePresentation;
        _imageReplayMaterializer = imageReplayMaterializer;
        _viewModel.PasteActionAsync = PasteAsync;
        _viewModel.CopyFileActionAsync = CopyFileAsync;
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

        if (_viewModel.CopyFileActionAsync == CopyFileAsync)
        {
            _viewModel.CopyFileActionAsync = null;
        }

    }

    private Task PasteAsync(ClipboardItemViewModel item) =>
        ExecuteAsync(item, forceCopyOnly: false);

    private Task CopyFileAsync(ClipboardItemViewModel item) =>
        ExecuteAsync(item, forceCopyOnly: true);

    private async Task ExecuteAsync(
        ClipboardItemViewModel item,
        bool forceCopyOnly)
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
        ClipboardFileTransferIntent? actualIntent = null;
        ClipboardImageReplayMaterialization? imageMaterialization = null;
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

            actualIntent = payload.FileIntent;
            if (payload.Kind is ClipboardContentKind.FileList)
            {
                if (payload.FileReferences.Count == 0 || payload.FileIntent is null)
                {
                    Finish(generation, ClipboardPasteState.Failed, "此条文件历史已损坏。");
                    return;
                }

                if (_filePresentation is not null)
                {
                    var presentation = await _filePresentation
                        .InspectAsync(payload.FileReferences, 48, cancellationToken)
                        .ConfigureAwait(true);
                    if (presentation.Availability is ClipboardFileAvailability.Missing)
                    {
                        Finish(
                            generation,
                            ClipboardPasteState.Failed,
                            "源文件已失效，未改写系统剪贴板。");
                        return;
                    }
                }

                actualIntent = forceCopyOnly ||
                    payload.FileIntent is not ClipboardFileTransferIntent.Move
                    ? ClipboardFileTransferIntent.Copy
                    : ClipboardFileTransferIntent.Move;
                payload = payload with
                {
                    FileIntent = actualIntent,
                    SemanticHash = ClipboardFileSemanticIdentity.Compute(
                        actualIntent.Value,
                        payload.FileReferences),
                };
            }
            else if (forceCopyOnly)
            {
                Finish(generation, ClipboardPasteState.Failed, "只有文件历史支持强制复制。");
                return;
            }
            else if (payload.Kind is ClipboardContentKind.Image)
            {
                if (_imageReplayMaterializer is null)
                {
                    Finish(
                        generation,
                        ClipboardPasteState.Failed,
                        "图片原图回放尚未启用。");
                    return;
                }

                imageMaterialization = await _imageReplayMaterializer
                    .MaterializeAsync(payload, cancellationToken)
                    .ConfigureAwait(true);
            }

            RequireAdvance(
                generation,
                ClipboardPasteState.Materializing,
                ClipboardPasteState.WritingClipboard);
            var operationId = Guid.NewGuid();
            receipt = imageMaterialization is null
                ? await _platform
                    .WriteAsync(payload, operationId, cancellationToken)
                    .ConfigureAwait(true)
                : await _platform
                    .WriteImageAsync(
                        imageMaterialization,
                        operationId,
                        cancellationToken)
                    .ConfigureAwait(true);

            if (forceCopyOnly)
            {
                RequireAdvance(
                    generation,
                    ClipboardPasteState.WritingClipboard,
                    ClipboardPasteState.VerifyingCurrent);
                if (!await _platform
                        .IsCurrentAsync(receipt.Value, cancellationToken)
                        .ConfigureAwait(true))
                {
                    Finish(
                        generation,
                        ClipboardPasteState.Failed,
                        "剪贴板已被其他程序更新，文件复制未保持为当前内容。");
                    return;
                }

                Finish(
                    generation,
                    ClipboardPasteState.Completed,
                    "已复制文件列表；不会移动源文件。");
                return;
            }

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
                    ManualPasteStatus(actualIntent, "目标窗口未确认恢复"));
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
                    ManualPasteStatus(actualIntent, "目标输入焦点发生变化"));
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
                    : actualIntent is ClipboardFileTransferIntent.Move
                        ? $"已写入剪切，但自动粘贴未完成：{exception.Message}；手动粘贴将移动源文件。"
                        : $"内容已复制，但自动粘贴未完成：{exception.Message}");
        }
        finally
        {
            try
            {
                imageMaterialization?.Dispose();
            }
            finally
            {
                _actionGate.Release();
            }
        }
    }

    private static string ManualPasteStatus(
        ClipboardFileTransferIntent? intent,
        string reason) => intent is ClipboardFileTransferIntent.Move
        ? $"已写入剪切；{reason}，请手动粘贴，粘贴将移动源文件。"
        : $"已复制到系统剪贴板；{reason}，请手动粘贴。";

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
        if (_stateMachine.Finish(generation, terminal) && !_disposed)
        {
            _viewModel.SetActionStatus(status);
        }
    }
}
