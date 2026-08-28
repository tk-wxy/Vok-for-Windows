namespace Vok.Hosting;

public interface IWorkspaceWindow
{
    event EventHandler? Activated;

    event EventHandler? ApplicationDeactivated;

    event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;

    event EventHandler? InputAnchorFocused;

    event EventHandler<WorkspaceExternalDragStartedEventArgs>? ExternalDragStarted;

    event EventHandler<WorkspaceExternalDragEndedEventArgs>? ExternalDragEnded;

    bool IsVisible { get; }

    bool HasInputAnchorFocus { get; }

    nint EnsureHandle();

    void Show();

    void Hide();

    bool Activate();

    bool FocusInputAnchor();

    void PostInput(Action action);

    void PostNormal(Action action);

    void PostBackground(Action action);

    IDisposable ScheduleNormal(TimeSpan dueTime, Action action);
}
