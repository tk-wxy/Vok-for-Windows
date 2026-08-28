namespace Vok.Core.Platform;

public interface IDisplayWorkAreaProvider
{
    IReadOnlyList<DisplayWorkArea> GetWorkAreas();
}
