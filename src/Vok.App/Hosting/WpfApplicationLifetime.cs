using System.Windows;

namespace Vok.Hosting;

public sealed class WpfApplicationLifetime : IApplicationLifetime
{
    public void Shutdown() => Application.Current?.Shutdown();
}
