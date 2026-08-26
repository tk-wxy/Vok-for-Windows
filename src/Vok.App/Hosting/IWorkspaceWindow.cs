namespace Vok.Hosting;

public interface IWorkspaceWindow
{
    event EventHandler? Activated;

    event EventHandler? ApplicationDeactivated;

    event EventHandler<WorkspaceHideRequestedEventArgs>? HideRequested;

    event EventHandler? InputAnchorFocused;

    bool IsVisible { get; }

    bool HasInputAnchorFocus { get; }

    nint EnsureHandle();

    void Show();

    void Hide();

    bool Activate();

    bool FocusInputAnchor();

    void PostInput(Action action);

    void PostNormal(Action action);
}
