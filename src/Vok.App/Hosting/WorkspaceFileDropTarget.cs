using Vok.Features.Launcher;
using Vok.Features.Stage;

namespace Vok.Hosting;

internal enum WorkspaceFileDropTargetKind
{
    None,
    Launcher,
    Stage,
}

internal interface IWorkspaceFileDropTarget
{
    WorkspaceFileDropTargetKind Kind { get; }

    bool IsAvailable { get; }

    void CancelInteractions();

    void SetExternalDragState(bool sessionActive, bool targeted);

    Task ReceiveAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    void ReportDispatchFailure(Exception exception);
}

internal sealed class LauncherWorkspaceFileDropTarget(
    LauncherView view,
    LauncherViewModel? viewModel) : IWorkspaceFileDropTarget
{
    public WorkspaceFileDropTargetKind Kind => WorkspaceFileDropTargetKind.Launcher;

    public bool IsAvailable => viewModel is not null;

    public void CancelInteractions() => view.CancelAllInteractions();

    public void SetExternalDragState(bool sessionActive, bool targeted) =>
        view.SetExternalDragActive(sessionActive, targeted);

    public Task ReceiveAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        viewModel?.AddDroppedPathsAsync(paths, cancellationToken) ?? Task.CompletedTask;

    public void ReportDispatchFailure(Exception exception) =>
        viewModel?.RecordError("添加启动项失败", exception);
}

internal sealed class StageWorkspaceFileDropTarget(
    StageView view,
    StageViewModel? viewModel) : IWorkspaceFileDropTarget
{
    public WorkspaceFileDropTargetKind Kind => WorkspaceFileDropTargetKind.Stage;

    public bool IsAvailable => viewModel is not null;

    public void CancelInteractions()
    {
    }

    public void SetExternalDragState(bool sessionActive, bool targeted) =>
        view.SetExternalDragState(sessionActive, targeted);

    public Task ReceiveAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        viewModel?.AddDroppedPathsAsync(paths, cancellationToken) ?? Task.CompletedTask;

    public void ReportDispatchFailure(Exception exception) =>
        viewModel?.RecordDispatchFailure(exception);
}
