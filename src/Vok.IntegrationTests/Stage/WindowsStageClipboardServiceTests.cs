using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Stage;
using Xunit;

namespace Vok.IntegrationTests.Stage;

public sealed class WindowsStageClipboardServiceTests
{
    [Fact]
    public async Task CopyPathWritesExactUnicodeTextThroughTheSharedClipboardService()
    {
        var platform = new RecordingClipboardPlatformService();
        var service = new WindowsStageClipboardService(platform);
        const string path = @"C:\中转目录\Ä exact target.lnk";

        await service.CopyPathTextAsync(
            path,
            TestContext.Current.CancellationToken);
        await service.CopyPathTextAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, platform.Writes.Count);
        var first = platform.Writes[0];
        var second = platform.Writes[1];
        Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(ClipboardContentKind.Text, first.Payload.Kind);
        Assert.Equal(path, first.Payload.PlainText);
        Assert.Equal(first.Payload.SemanticHash, second.Payload.SemanticHash);
        Assert.False(string.IsNullOrWhiteSpace(first.Payload.SemanticHash));
        var representation = Assert.Single(first.Payload.Representations);
        Assert.Equal(ClipboardRepresentationKind.UnicodeText, representation.Kind);
        Assert.Equal(path, representation.InlineText);
        Assert.Equal(
            Encoding.Unicode.GetByteCount(path) + sizeof(char),
            representation.ByteLength);
    }

    private sealed class RecordingClipboardPlatformService : IClipboardPlatformService
    {
        public List<(ClipboardReplayPayload Payload, Guid OperationId)> Writes { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IAsyncEnumerable<ClipboardCaptureResult> WatchAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClipboardWriteReceipt> WriteAsync(
            ClipboardReplayPayload payload,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add((payload, operationId));
            return Task.FromResult(new ClipboardWriteReceipt(
                RuntimeSequence: checked((uint)Writes.Count),
                operationId,
                payload.SemanticHash));
        }

        public Task<bool> IsCurrentAsync(
            ClipboardWriteReceipt receipt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public ClipboardDiagnosticSnapshot GetDiagnosticSnapshot() =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
