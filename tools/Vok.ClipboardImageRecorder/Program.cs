using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;

namespace Vok.ClipboardImageRecorder;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> Main(string[] args)
    {
        if (!RecorderOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.SelfTest)
        {
            ClipboardImageMetadataInspector.RunSelfTest();
            ControlledImageSamples.RunSelfTest();
            if (!RecorderOptions.TryParse(
                    ["--labels", "a,b", "--include-non-image"],
                    out var parsedOptions,
                    out _) ||
                parsedOptions.CaptureCount != 2 ||
                !parsedOptions.IncludeNonImage)
            {
                throw new InvalidOperationException(
                    "Clipboard image recorder option self-test failed.");
            }

            Console.WriteLine("Clipboard image metadata inspector self-test passed.");
            return 0;
        }

        if (options.PrepareSamples)
        {
            var samples = ControlledImageSamples.WriteToTemporaryDirectory();
            Console.WriteLine($"Sample directory: {samples.DirectoryPath}");
            Console.WriteLine($"Explorer file-copy sample: {samples.TransparentPngPath}");
            Console.WriteLine($"Browser sample page: {samples.HtmlPath}");
            Console.WriteLine($"8K browser sample: {samples.Transparent8KPngPath}");
            return 0;
        }

        try
        {
            return await RecordAsync(options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RecordAsync(RecorderOptions options)
    {
        var directory = Path.GetDirectoryName(options.OutputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The recording path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await using var output = new FileStream(
            options.OutputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false)
        {
            AutoFlush = true,
        };
        using var timeout = new CancellationTokenSource(options.Timeout);
        using var userCancellation = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            userCancellation.Token);
        var cancelledByUser = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Volatile.Write(ref cancelledByUser, 1);
            userCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var startedUtc = DateTimeOffset.UtcNow;
        var captured = 0;
        try
        {
            await WriteLineAsync(writer, new
            {
                type = "session",
                schema = 1,
                startedUtc,
                requestedCaptures = options.CaptureCount,
                timeoutSeconds = options.Timeout.TotalSeconds,
                labels = options.Labels,
                options.IncludeNonImage,
                privacy = "Format metadata only; clipboard payload bytes and hashes are not serialized.",
            }).ConfigureAwait(false);

            Console.WriteLine($"Recording image clipboard metadata to: {options.OutputPath}");
            Console.WriteLine("Clipboard payload bytes are inspected in memory but never written to the report.");
            PrintWaitingLabel(options, captured);

            using var service = new WindowsClipboardPlatformService();
            await service.StartAsync(lifetime.Token).ConfigureAwait(false);
            await foreach (var capture in service
                               .WatchAsync(lifetime.Token)
                               .ConfigureAwait(false))
            {
                using (capture)
                {
                    if (!options.IncludeNonImage && !HasImageFormat(capture))
                    {
                        continue;
                    }

                    var label = LabelFor(options, captured);
                    captured++;
                    var record = new
                    {
                        type = "capture",
                        ordinal = captured,
                        label,
                        capture.RuntimeSequence,
                        capture.CapturedUtc,
                        disposition = capture.Disposition.ToString(),
                        formats = capture.Formats.Select(SummarizeFormat).ToArray(),
                        error = capture.Error,
                    };
                    await WriteLineAsync(writer, record).ConfigureAwait(false);
                    Console.WriteLine($"Captured {captured}/{options.CaptureCount}: {label}");
                    if (captured >= options.CaptureCount)
                    {
                        break;
                    }

                    PrintWaitingLabel(options, captured);
                }
            }

            await WriteLineAsync(writer, new
            {
                type = "summary",
                completedUtc = DateTimeOffset.UtcNow,
                captured,
                requested = options.CaptureCount,
                completed = captured == options.CaptureCount,
                timedOut = false,
                cancelled = false,
            }).ConfigureAwait(false);
            return captured == options.CaptureCount ? 0 : 3;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            await WriteLineAsync(writer, new
            {
                type = "summary",
                completedUtc = DateTimeOffset.UtcNow,
                captured,
                requested = options.CaptureCount,
                completed = false,
                timedOut = timeout.IsCancellationRequested,
                cancelled = Volatile.Read(ref cancelledByUser) != 0,
            }).ConfigureAwait(false);
            var wasCancelledByUser = Volatile.Read(ref cancelledByUser) != 0;
            Console.Error.WriteLine(wasCancelledByUser
                ? "Recording cancelled by user."
                : $"Recording timed out after {options.Timeout.TotalSeconds:0} seconds.");
            return wasCancelledByUser ? 130 : 3;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static object SummarizeFormat(ClipboardFormatSnapshot format)
    {
        var isImage = IsImage(format.RepresentationKind);
        var header = isImage && format.CaptureState is ClipboardFormatCaptureState.Captured
            ? InspectCapturedFormat(format)
            : null;
        return new
        {
            format.SourceOrder,
            format.FormatName,
            representation = format.RepresentationKind.ToString(),
            role = format.Role.ToString(),
            state = format.CaptureState.ToString(),
            byteLength = isImage
                ? (long?)(format.SourceByteLength ?? format.ByteLength)
                : null,
            header,
        };
    }

    private static ClipboardImageHeaderMetadata? InspectCapturedFormat(
        ClipboardFormatSnapshot format)
    {
        if (!format.Payload.IsEmpty)
        {
            return ClipboardImageMetadataInspector.Inspect(
                format.RepresentationKind,
                format.Payload.Span);
        }

        const int maximumInspectionBytes = 1024 * 1024;
        var length = checked((int)Math.Min(
            format.PayloadByteLength,
            maximumInspectionBytes));
        var bytes = new byte[length];
        using var stream = format.OpenPayloadReadStream();
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return ClipboardImageMetadataInspector.Inspect(
            format.RepresentationKind,
            bytes.AsSpan(0, read));
    }

    private static bool HasImageFormat(ClipboardCaptureResult capture) =>
        capture.Formats.Any(static format =>
            IsImage(format.RepresentationKind) &&
            format.CaptureState is not ClipboardFormatCaptureState.NotRequested);

    private static bool IsImage(ClipboardRepresentationKind kind) => kind is
        ClipboardRepresentationKind.Png or
        ClipboardRepresentationKind.DibV5 or
        ClipboardRepresentationKind.Dib or
        ClipboardRepresentationKind.Bitmap;

    private static string LabelFor(RecorderOptions options, int zeroBasedIndex) =>
        zeroBasedIndex < options.Labels.Count
            ? options.Labels[zeroBasedIndex]
            : $"image-{zeroBasedIndex + 1}";

    private static void PrintWaitingLabel(RecorderOptions options, int zeroBasedIndex) =>
        Console.WriteLine($"Waiting for: {LabelFor(options, zeroBasedIndex)}");

    private static Task WriteLineAsync(StreamWriter writer, object value) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: Vok.ClipboardImageRecorder " +
            "[--output <jsonl>] [--count <1-100>] " +
            "[--timeout-seconds <1-3600>] [--labels <a,b,c>] " +
            "[--include-non-image] [--self-test] [--prepare-samples]");
    }
}
