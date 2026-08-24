using System.Windows.Media;

namespace Vok.Hosting;

public sealed class WpfWorkspaceFrameSource : IWorkspaceFrameSource
{
    public event EventHandler? Rendering
    {
        add => CompositionTarget.Rendering += value;
        remove => CompositionTarget.Rendering -= value;
    }
}
