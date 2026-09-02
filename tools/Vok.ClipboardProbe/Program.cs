using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.ClipboardProbe;

internal static class Program
{
    private const int DefaultExtendedImageRounds = 2;
    private const uint UnicodeText = 13;
    private const uint Dib = 8;
    private const uint DibV5 = 17;
    private const uint FileDrop = 15;
    private const string HtmlFormatName = "HTML Format";
    private const string PngFormatName = "PNG";
    private const string ExcludeFormatName =
        "ExcludeClipboardContentFromMonitorProcessing";
    private const string PreferredDropEffectName = "Preferred DropEffect";
    private const string FileGroupDescriptorWName = "FileGroupDescriptorW";
    private const string FileContentsName = "FileContents";

    public static async Task<int> Main(string[] args)
    {
        var extendedImageRounds = ReadExtendedImageRounds(args);
        var runExtendedImageMatrix = extendedImageRounds is not null;
        using var timeout = new CancellationTokenSource(
            runExtendedImageMatrix
                ? TimeSpan.FromMinutes(10)
                : TimeSpan.FromSeconds(20));
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
            using var captures = new OwnedCaptures();

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
            var immediate = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            await producer.WriteDelayedAsync(
                [
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("delayed\0")),
                ],
                timeout.Token);
            var delayed = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            var exclude = producer.RegisterFormat(ExcludeFormatName);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(exclude, [1]),
                    new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("must-not-be-read\0")),
                ],
                timeout.Token);
            var excluded = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

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
            var selfWrite = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            await producer.WriteImmediateAsync(
                [new ProducerFormat(
                    UnicodeText,
                    Encoding.Unicode.GetBytes("external-overwrite\0"))],
                timeout.Token);
            var receiptAfterExternalWrite = await service.IsCurrentAsync(
                receipt,
                timeout.Token);
            var externalOverwrite = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

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
            var fileCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
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
                fileSelfWrite = captures.Track(
                    await ReadNextAsync(snapshots, timeout.Token));

                await producer.WriteImmediateAsync(
                    [new ProducerFormat(
                        UnicodeText,
                        Encoding.Unicode.GetBytes("file-external-overwrite\0"))],
                    timeout.Token);
                fileReceiptAfterExternalWrite = await service.IsCurrentAsync(
                    fileReceipt.Value,
                    timeout.Token);
                _ = captures.Track(
                    await ReadNextAsync(snapshots, timeout.Token));
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
            var ansiCopyCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
            var ansiCopyFiles = new ClipboardContentNormalizer()
                .Normalize(ansiCopyCapture) as ClipboardFileListContent;

            await producer.WriteImmediateAsync(
                [new ProducerFormat(FileDrop, CreateFileDrop(filePaths))],
                timeout.Token);
            var unknownCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
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
            var malformedCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
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
            var virtualFileCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            var png = producer.RegisterFormat(PngFormatName);
            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(png, ControlledImagePayloads.CreatePng()),
                    new ProducerFormat(DibV5, ControlledImagePayloads.CreateDibV5()),
                    new ProducerFormat(Dib, ControlledImagePayloads.CreateDib24()),
                ],
                timeout.Token);
            var multiImageCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
            var imageRouterBaseline = new ClipboardContentNormalizer()
                .Normalize(multiImageCapture);

            await producer.WriteBitmapAsync(
                ControlledImagePayloads.Width,
                ControlledImagePayloads.Height,
                ControlledImagePayloads.CreateBottomUpBgraPixels(),
                timeout.Token);
            var bitmapOnlyCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            await producer.WriteImmediateAsync(
                [
                    new ProducerFormat(DibV5, ControlledImagePayloads.CreateDibV5()),
                    new ProducerFormat(Dib, ControlledImagePayloads.CreateDib24()),
                ],
                timeout.Token);
            var controlledDibCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            await producer.WriteImmediateAsync(
                [new ProducerFormat(Dib, [1, 2, 3, 4])],
                timeout.Token);
            var malformedImageCapture = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));
            var malformedImageContent = new ClipboardContentNormalizer()
                .Normalize(malformedImageCapture);

            using var imageExecutor = new WindowsClipboardImageExecutor();
            using var normalizedPng = await imageExecutor.NormalizeAsync(
                multiImageCapture,
                timeout.Token);
            using var normalizedDib = await imageExecutor.NormalizeAsync(
                controlledDibCapture,
                timeout.Token);
            using var normalizedBitmap = await imageExecutor.NormalizeAsync(
                bitmapOnlyCapture,
                timeout.Token);
            var controlledPixelsEquivalent = string.Equals(
                normalizedPng.SemanticHash,
                normalizedDib.SemanticHash,
                StringComparison.Ordinal);

            var canonicalPng = ReadAll(normalizedPng.OpenCanonicalPngReadStream());
            using var imageReplay = new ClipboardImageReplayMaterialization(
                normalizedPng.SemanticHash,
                normalizedPng.PixelWidth,
                normalizedPng.PixelHeight,
                normalizedPng.HasAlpha,
                normalizedPng.DpiXMilli,
                normalizedPng.DpiYMilli,
                new ProbeLease(canonicalPng),
                new ProbeLease(ControlledImagePayloads.CreateDibV5()));
            var imageReceipt = await service.WriteImageAsync(
                imageReplay,
                Guid.NewGuid(),
                timeout.Token);
            var imageReceiptWasCurrent = await service.IsCurrentAsync(
                imageReceipt,
                timeout.Token);
            var imageSelfWrite = captures.Track(
                await ReadNextAsync(snapshots, timeout.Token));

            await producer.WriteImmediateAsync(
                [new ProducerFormat(
                    UnicodeText,
                    Encoding.Unicode.GetBytes("image-external-overwrite\0"))],
                timeout.Token);
            var imageReceiptAfterExternalWrite = await service.IsCurrentAsync(
                imageReceipt,
                timeout.Token);
            _ = captures.Track(await ReadNextAsync(snapshots, timeout.Token));

            ExtendedImageMatrixReport? extendedImageMatrix = null;
            if (extendedImageRounds is int rounds)
            {
                extendedImageMatrix = await ExtendedImageMatrixProbe.RunAsync(
                    producer,
                    png,
                    snapshots,
                    rounds,
                    timeout.Token);
            }

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
                    format.Payload.IsEmpty) &&
                multiImageCapture.Disposition is ClipboardCaptureDisposition.Captured &&
                HasCapturedFormat(multiImageCapture, ClipboardRepresentationKind.Png) &&
                multiImageCapture.Formats.Where(format =>
                    format.RepresentationKind is
                        ClipboardRepresentationKind.DibV5 or
                        ClipboardRepresentationKind.Dib).All(format =>
                    format.CaptureState is ClipboardFormatCaptureState.NotRequested) &&
                imageRouterBaseline is null &&
                bitmapOnlyCapture.Disposition is ClipboardCaptureDisposition.Captured &&
                bitmapOnlyCapture.Formats.Any(format =>
                    format.RepresentationKind is ClipboardRepresentationKind.Bitmap &&
                    format.CaptureState is ClipboardFormatCaptureState.MetadataOnly) &&
                bitmapOnlyCapture.Formats.Any(format =>
                    format.RepresentationKind is
                        ClipboardRepresentationKind.DibV5 or
                        ClipboardRepresentationKind.Dib &&
                    format.CaptureState is ClipboardFormatCaptureState.Captured) &&
                controlledDibCapture.Disposition is ClipboardCaptureDisposition.Captured &&
                HasCapturedFormat(
                    controlledDibCapture,
                    ClipboardRepresentationKind.DibV5) &&
                normalizedPng.HasAlpha &&
                normalizedDib.HasAlpha &&
                !normalizedBitmap.HasAlpha &&
                controlledPixelsEquivalent &&
                imageReceiptWasCurrent &&
                imageSelfWrite.Disposition is ClipboardCaptureDisposition.SelfWrite &&
                !imageReceiptAfterExternalWrite &&
                malformedImageCapture.Disposition is ClipboardCaptureDisposition.Unsupported &&
                malformedImageCapture.Formats.Any(format =>
                    format.RepresentationKind is
                        ClipboardRepresentationKind.DibV5 or
                        ClipboardRepresentationKind.Dib &&
                    format.CaptureState is ClipboardFormatCaptureState.Failed) &&
                malformedImageContent is null &&
                extendedImageMatrix?.Success is not false;
            var report = new
            {
                schema = 6,
                reportVersion = 7,
                isolatedWindowStation = true,
                extendedImageMatrixRequested = runExtendedImageMatrix,
                extendedImageRounds,
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
                imageFormats = new
                {
                    multiRepresentation = Summarize(multiImageCapture),
                    routerNotImplemented = imageRouterBaseline is null,
                    bitmapOnly = Summarize(bitmapOnlyCapture),
                    bitmapWasCataloged = bitmapOnlyCapture.Formats.Any(format =>
                        format.RepresentationKind is ClipboardRepresentationKind.Bitmap),
                    synthesizedDibWasCaptured = bitmapOnlyCapture.Formats.Any(format =>
                        format.RepresentationKind is
                            ClipboardRepresentationKind.DibV5 or
                            ClipboardRepresentationKind.Dib &&
                        format.CaptureState is ClipboardFormatCaptureState.Captured),
                    decoder = new
                    {
                        pngDecoded = normalizedPng.PixelWidth == ControlledImagePayloads.Width &&
                            normalizedPng.PixelHeight == ControlledImagePayloads.Height,
                        dibV5Decoded = normalizedDib.PixelWidth == ControlledImagePayloads.Width &&
                            normalizedDib.PixelHeight == ControlledImagePayloads.Height,
                        bitmapSynthesisDecoded = normalizedBitmap.PixelWidth ==
                            ControlledImagePayloads.Width &&
                            normalizedBitmap.PixelHeight == ControlledImagePayloads.Height,
                        controlledPixelsEquivalent,
                        pngHasAlpha = normalizedPng.HasAlpha,
                        dibV5HasAlpha = normalizedDib.HasAlpha,
                        bitmapSynthesisHasAlpha = normalizedBitmap.HasAlpha,
                    },
                    replay = new
                    {
                        imageReceipt.RuntimeSequence,
                        imageReceiptWasCurrent,
                        selfWriteDisposition = imageSelfWrite.Disposition.ToString(),
                        imageReceiptAfterExternalWrite,
                        canonicalBytes = canonicalPng.Length,
                        dibV5Bytes = imageReplay.DibV5ByteLength,
                    },
                    malformedDib = Summarize(malformedImageCapture),
                    malformedRouterRejected = malformedImageContent is null,
                },
                extendedImageMatrix,
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

    private static int? ReadExtendedImageRounds(string[] args)
    {
        var runExtended = args.Contains(
            "--extended-image-matrix",
            StringComparer.OrdinalIgnoreCase);
        var roundsIndex = Array.FindIndex(
            args,
            static value => string.Equals(
                value,
                "--extended-image-rounds",
                StringComparison.OrdinalIgnoreCase));
        if (roundsIndex < 0)
        {
            return runExtended ? DefaultExtendedImageRounds : null;
        }

        if (roundsIndex == args.Length - 1 ||
            !int.TryParse(args[roundsIndex + 1], out var rounds) ||
            rounds is < 1 or > 50)
        {
            throw new ArgumentException(
                "--extended-image-rounds requires an integer from 1 through 50.");
        }

        return rounds;
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
            storage = format.PayloadStorageKind.ToString(),
            format.ReservedByteLength,
        }),
        result.Error,
    };

    private static bool HasCapturedFormat(
        ClipboardCaptureResult capture,
        ClipboardRepresentationKind kind) => capture.Formats.Any(format =>
            format.RepresentationKind == kind &&
            format.CaptureState is ClipboardFormatCaptureState.Captured &&
            format.PayloadByteLength > 0);

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    private sealed class ProbeLease(byte[] bytes) : IClipboardPayloadLease
    {
        private int _disposed;

        public long Length => bytes.LongLength;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new MemoryStream(bytes, writable: false);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class OwnedCaptures : IDisposable
    {
        private readonly List<ClipboardCaptureResult> _captures = [];

        public ClipboardCaptureResult Track(ClipboardCaptureResult capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            _captures.Add(capture);
            return capture;
        }

        public void Dispose()
        {
            for (var index = _captures.Count - 1; index >= 0; index--)
            {
                _captures[index].Dispose();
            }

            _captures.Clear();
        }
    }

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
