using System.Text;
using System.Text.Json;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.ClipboardProbe;

internal static class Program
{
    private const uint UnicodeText = 13;
    private const string HtmlFormatName = "HTML Format";
    private const string ExcludeFormatName =
        "ExcludeClipboardContentFromMonitorProcessing";

    public static async Task<int> Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var station = IsolatedWindowStation.Create();
            using var service = new WindowsClipboardPlatformService();
            using var producer = new ControlledClipboardProducer();
            await service.StartAsync(timeout.Token);
            await producer.StartAsync(timeout.Token);
            await using var snapshots = service
                .WatchAsync(timeout.Token)
                .GetAsyncEnumerator(timeout.Token);

            var html = producer.RegisterFormat(HtmlFormatName);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(
                        html,
                        Encoding.UTF8.GetBytes("<b>isolated</b>\0")),
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("isolated\0")),
                ],
                timeout.Token);
            var immediate = await ReadNextAsync(snapshots, timeout.Token);

            await producer.WriteDelayedAsync(
                [
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("delayed\0")),
                ],
                timeout.Token);
            var delayed = await ReadNextAsync(snapshots, timeout.Token);

            var exclude = producer.RegisterFormat(ExcludeFormatName);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(exclude, [1]),
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("must-not-be-read\0")),
                ],
                timeout.Token);
            var excluded = await ReadNextAsync(snapshots, timeout.Token);

            var replayPayload = new ClipboardReplayPayload(
                ClipboardContentKind.Text,
                new string('c', 64),
                [
                    new ClipboardStoredRepresentation(
                        ClipboardRepresentationKind.Html,
                        300,
                        "<i>replay</i>",
                        13),
                    new ClipboardStoredRepresentation(
                        ClipboardRepresentationKind.Rtf,
                        200,
                        "{\\rtf1 replay}",
                        14),
                    new ClipboardStoredRepresentation(
                        ClipboardRepresentationKind.UnicodeText,
                        100,
                        "replay",
                        14),
                ]);
            var receipt = await service.WriteAsync(
                replayPayload,
                Guid.NewGuid(),
                timeout.Token);
            var receiptWasCurrent = await service.IsCurrentAsync(receipt, timeout.Token);
            var selfWrite = await ReadNextAsync(snapshots, timeout.Token);

            await producer.WriteImmediateAsync(
                [new ProducerFormat(
                    UnicodeText,
                    Encoding.Unicode.GetBytes("external-overwrite\0"))],
                timeout.Token);
            var receiptAfterExternalWrite = await service.IsCurrentAsync(
                receipt,
                timeout.Token);
            var externalOverwrite = await ReadNextAsync(snapshots, timeout.Token);

            var success =
                immediate.Disposition is ClipboardCaptureDisposition.Captured &&
                immediate.Formats.Any(format =>
                    format.RepresentationKind is ClipboardRepresentationKind.Html &&
                    format.CaptureState is ClipboardFormatCaptureState.Captured) &&
                immediate.Formats.Any(format =>
                    format.RepresentationKind is ClipboardRepresentationKind.UnicodeText &&
                    format.CaptureState is ClipboardFormatCaptureState.Captured) &&
                delayed.Disposition is ClipboardCaptureDisposition.Captured &&
                delayed.Formats.Any(format =>
                    format.RepresentationKind is ClipboardRepresentationKind.UnicodeText &&
                    Encoding.Unicode.GetString(format.Payload.Span) == "delayed\0") &&
                excluded.Disposition is ClipboardCaptureDisposition.Excluded &&
                excluded.Formats.All(format => format.Payload.IsEmpty) &&
                receiptWasCurrent &&
                selfWrite.Disposition is ClipboardCaptureDisposition.SelfWrite &&
                !receiptAfterExternalWrite &&
                externalOverwrite.Disposition is ClipboardCaptureDisposition.Captured &&
                externalOverwrite.Formats.Any(format =>
                    format.RepresentationKind is ClipboardRepresentationKind.UnicodeText &&
                    Encoding.Unicode.GetString(format.Payload.Span) == "external-overwrite\0");
            var report = new
            {
                schema = 2,
                isolatedWindowStation = true,
                immediate = Summarize(immediate),
                delayed = Summarize(delayed),
                excluded = Summarize(excluded),
                writeReceipt = new
                {
                    receipt.RuntimeSequence,
                    receiptWasCurrent,
                    selfWriteDisposition = selfWrite.Disposition.ToString(),
                    receiptAfterExternalWrite,
                },
                externalOverwrite = Summarize(externalOverwrite),
                diagnostics = service.GetDiagnosticSnapshot(),
                success,
            };
            Console.WriteLine(JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static async Task<ClipboardCaptureResult> ReadNextAsync(
        IAsyncEnumerator<ClipboardCaptureResult> snapshots,
        CancellationToken cancellationToken)
    {
        if (!await snapshots.MoveNextAsync().AsTask().WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException("Clipboard capture stream completed early.");
        }

        return snapshots.Current;
    }

    private static object Summarize(ClipboardCaptureResult result) => new
    {
        result.RuntimeSequence,
        disposition = result.Disposition.ToString(),
        formats = result.Formats.Select(format => new
        {
            format.SourceOrder,
            format.FormatName,
            representation = format.RepresentationKind.ToString(),
            role = format.Role.ToString(),
            state = format.CaptureState.ToString(),
            format.ByteLength,
        }),
        result.Error,
    };
}
