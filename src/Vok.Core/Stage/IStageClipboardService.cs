namespace Vok.Core.Stage;

public interface IStageClipboardService
{
    Task CopyPathTextAsync(
        string targetValue,
        CancellationToken cancellationToken = default);
}
