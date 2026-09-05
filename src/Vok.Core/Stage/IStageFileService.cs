namespace Vok.Core.Stage;

public interface IStageFileService
{
    Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);

    Task<StageFileAvailability> InspectAsync(
        StageItem item,
        CancellationToken cancellationToken = default);

    Task OpenAsync(
        StageItem item,
        CancellationToken cancellationToken = default);

    Task RevealInExplorerAsync(
        StageItem item,
        CancellationToken cancellationToken = default);
}
