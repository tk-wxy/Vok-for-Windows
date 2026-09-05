using System.Text;
using Vok.Core.Clipboard;
using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.Infrastructure.Windows.Stage;

public sealed class WindowsStageClipboardService(
    IClipboardPlatformService clipboard) : IStageClipboardService
{
    private readonly IClipboardPlatformService _clipboard =
        clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    public async Task CopyPathTextAsync(
        string targetValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetValue);
        var representation = new ClipboardStoredRepresentation(
            ClipboardRepresentationKind.UnicodeText,
            FidelityRank: 100,
            targetValue,
            checked(Encoding.Unicode.GetByteCount(targetValue) + sizeof(char)));
        IReadOnlyList<ClipboardStoredRepresentation> representations = [representation];
        var payload = new ClipboardReplayPayload(
            ClipboardContentKind.Text,
            ClipboardRepresentationSemanticHash.Compute(representations),
            representations);
        _ = await _clipboard
            .WriteAsync(payload, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);
    }
}
