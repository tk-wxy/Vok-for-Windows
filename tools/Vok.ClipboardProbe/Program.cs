using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.ClipboardProbe;

internal static class Program
{
    private const uint UnicodeText = 13;
    private const uint FileDrop = 15;
    private const string HtmlFormatName = "HTML Format";
    private const string ExcludeFormatName =
        "ExcludeClipboardContentFromMonitorProcessing";
    private const string PreferredDropEffectName = "Preferred DropEffect";
    private const string FileGroupDescriptorWName = "FileGroupDescriptorW";
    private const string FileContentsName = "FileContents";

    public static async Task<int> Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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

            var preferredDropEffect = producer.RegisterFormat(PreferredDropEffectName);
            var filePaths = new[]
            {
                Path.Combine(Path.GetTempPath(), "Vok Clipboard Probe", "报告.txt"),
                Path.Combine(Path.GetTempPath(), "Vok Clipboard Probe", "Folder"),
            };
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(FileDrop, CreateFileDrop(filePaths)),
                    new ProducerFormat(preferredDropEffect, DropEffect(2)),
                ],
                timeout.Token);
            var fileCapture = await ReadNextAsync(snapshots, timeout.Token);
            var normalizedFiles = new ClipboardContentNormalizer()
                .Normalize(fileCapture) as ClipboardFileListContent;
            ClipboardWriteReceipt? fileReceipt = null;
            var fileReceiptWasCurrent = false;
            ClipboardCaptureResult? fileSelfWrite = null;
            var fileReceiptAfterExternalWrite = true;
            if (normalizedFiles is not null)
            {
                var fileReplay = new ClipboardReplayPayload(
                    ClipboardContentKind.FileList,
                    normalizedFiles.SemanticHash,
                    [],
                    normalizedFiles.Intent,
                    normalizedFiles.Files);
                fileReceipt = await service.WriteAsync(
                    fileReplay,
                    Guid.NewGuid(),
                    timeout.Token);
                fileReceiptWasCurrent = await service.IsCurrentAsync(
                    fileReceipt.Value,
                    timeout.Token);
                fileSelfWrite = await ReadNextAsync(snapshots, timeout.Token);

                await producer.WriteImmediateAsync(
                    [new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("file-external-overwrite\0"))],
                    timeout.Token);
                fileReceiptAfterExternalWrite = await service.IsCurrentAsync(
                    fileReceipt.Value,
                    timeout.Token);
                _ = await ReadNextAsync(snapshots, timeout.Token);
            }

            var ansiPaths = new[]
            {
                Path.Combine(Path.GetTempPath(), "Vok Clipboard Probe", "ansi.txt"),
            };
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(FileDrop, CreateFileDrop(ansiPaths, isWide: false)),
                    // Explorer COPY can publish COPY | LINK instead of exact COPY.
                    new ProducerFormat(preferredDropEffect, DropEffect(5)),
                ],
                timeout.Token);
            var ansiCopyCapture = await ReadNextAsync(snapshots, timeout.Token);
            var ansiCopyFiles = new ClipboardContentNormalizer()
                .Normalize(ansiCopyCapture) as ClipboardFileListContent;

            await producer.WriteImmediateAsync(
                [new ProducerFormat(FileDrop, CreateFileDrop(filePaths))],
                timeout.Token);
            var unknownCapture = await ReadNextAsync(snapshots, timeout.Token);
            var unknownFiles = new ClipboardContentNormalizer()
                .Normalize(unknownCapture) as ClipboardFileListContent;

            var malformedDrop = CreateFileDrop(filePaths);
            BinaryPrimitives.WriteUInt32LittleEndian(malformedDrop, uint.MaxValue);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(FileDrop, malformedDrop),
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("must-not-fallback\0")),
                ],
                timeout.Token);
            var malformedCapture = await ReadNextAsync(snapshots, timeout.Token);
            var malformedContent = new ClipboardContentNormalizer()
                .Normalize(malformedCapture);

            var fileGroupDescriptorW = producer.RegisterFormat(FileGroupDescriptorWName);
            var fileContents = producer.RegisterFormat(FileContentsName);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(fileGroupDescriptorW, [1, 2, 3, 4]),
                    new ProducerFormat(fileContents, [5, 6, 7, 8]),
                ],
                timeout.Token);
            var virtualFileCapture = await ReadNextAsync(snapshots, timeout.Token);

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
                    Encoding.Unicode.GetString(format.Payload.Span) == "external-overwrite\0") &&
                normalizedFiles is not null &&
                normalizedFiles.Intent is ClipboardFileTransferIntent.Move &&
                normalizedFiles.Files.Select(static file => file.CanonicalPath)
                    .SequenceEqual(filePaths, StringComparer.OrdinalIgnoreCase) &&
                fileReceiptWasCurrent &&
                fileSelfWrite?.Disposition is ClipboardCaptureDisposition.SelfWrite &&
                !fileReceiptAfterExternalWrite &&
                ansiCopyFiles?.Intent is ClipboardFileTransferIntent.Copy &&
                ansiCopyFiles.Files.Select(static file => file.CanonicalPath)
                    .SequenceEqual(ansiPaths, StringComparer.OrdinalIgnoreCase) &&
                unknownFiles?.Intent is ClipboardFileTransferIntent.Unknown &&
                malformedCapture.Disposition is ClipboardCaptureDisposition.Captured &&
                malformedContent is null &&
                virtualFileCapture.Disposition is ClipboardCaptureDisposition.Unsupported &&
                virtualFileCapture.Formats.All(format =>
                    format.Role is ClipboardFormatRole.VirtualFile &&
                    format.CaptureState is ClipboardFormatCaptureState.MetadataOnly &&
                    format.Payload.IsEmpty);
            var report = new
            {
                schema = 4,
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
                fileRoundTrip = new
                {
                    capture = Summarize(fileCapture),
                    normalizedIntent = normalizedFiles?.Intent.ToString(),
                    itemCount = normalizedFiles?.Files.Count ?? 0,
                    receiptSequence = fileReceipt?.RuntimeSequence,
                    fileReceiptWasCurrent,
                    selfWriteDisposition = fileSelfWrite?.Disposition.ToString(),
                    fileReceiptAfterExternalWrite,
                },
                ansiCopy = new
                {
                    capture = Summarize(ansiCopyCapture),
                    normalizedIntent = ansiCopyFiles?.Intent.ToString(),
                    itemCount = ansiCopyFiles?.Files.Count ?? 0,
                },
                unknownIntent = new
                {
                    capture = Summarize(unknownCapture),
                    normalizedIntent = unknownFiles?.Intent.ToString(),
                    itemCount = unknownFiles?.Files.Count ?? 0,
                },
                malformedFileDrop = new
                {
                    capture = Summarize(malformedCapture),
                    routerRejectedWholeSnapshot = malformedContent is null,
                },
                virtualFiles = Summarize(virtualFileCapture),
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

    private static byte[] DropEffect(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] CreateFileDrop(
        IReadOnlyList<string> paths,
        bool isWide = true)
    {
        const int headerSize = 20;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = isWide
            ? Encoding.Unicode
            : Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        var encoded = paths.Select(encoding.GetBytes).ToArray();
        var terminatorBytes = isWide ? sizeof(char) : 1;
        var result = new byte[checked(
            headerSize + encoded.Sum(static path => path.Length) +
            terminatorBytes * (paths.Count + 1))];
        BinaryPrimitives.WriteUInt32LittleEndian(result, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), isWide ? 1 : 0);
        var offset = headerSize;
        foreach (var path in encoded)
        {
            path.CopyTo(result, offset);
            offset += path.Length + terminatorBytes;
        }

        return result;
    }
}
