using Vok.Core.Stage;

namespace Vok.Features.Stage;

public interface IStageRelinkTargetPicker
{
    StageRelinkSelection SelectTarget(
        StageItem item,
        CancellationToken cancellationToken = default);
}
