using System.Globalization;

namespace Vok.ClipboardImageRecorder;

internal sealed record RecorderOptions(
    string OutputPath,
    int CaptureCount,
    TimeSpan Timeout,
    IReadOnlyList<string> Labels,
    bool SelfTest,
    bool PrepareSamples,
    bool IncludeNonImage,
    bool ShowHelp)
{
    private const int DefaultCaptureCount = 6;
    private const int DefaultTimeoutSeconds = 300;

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out RecorderOptions options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var outputPath = DefaultOutputPath();
        var captureCount = DefaultCaptureCount;
        var timeoutSeconds = DefaultTimeoutSeconds;
        var labels = Array.Empty<string>();
        var selfTest = false;
        var prepareSamples = false;
        var includeNonImage = false;
        var showHelp = false;
        var countWasSpecified = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--output":
                    if (!TryReadValue(arguments, ref index, out outputPath))
                    {
                        return Fail("--output requires a path.", out options, out error);
                    }

                    break;
                case "--count":
                    if (!TryReadValue(arguments, ref index, out var countText) ||
                        !int.TryParse(
                            countText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out captureCount) ||
                        captureCount is < 1 or > 100)
                    {
                        return Fail("--count must be between 1 and 100.", out options, out error);
                    }

                    countWasSpecified = true;
                    break;
                case "--timeout-seconds":
                    if (!TryReadValue(arguments, ref index, out var timeoutText) ||
                        !int.TryParse(
                            timeoutText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 3600)
                    {
                        return Fail(
                            "--timeout-seconds must be between 1 and 3600.",
                            out options,
                            out error);
                    }

                    break;
                case "--labels":
                    if (!TryReadValue(arguments, ref index, out var labelsText))
                    {
                        return Fail("--labels requires comma-separated values.", out options, out error);
                    }

                    labels = labelsText
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (labels.Length == 0 || labels.Any(static label => label.Length > 80))
                    {
                        return Fail(
                            "--labels must contain labels no longer than 80 characters.",
                            out options,
                            out error);
                    }

                    if (!countWasSpecified)
                    {
                        captureCount = labels.Length;
                    }

                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--prepare-samples":
                    prepareSamples = true;
                    break;
                case "--include-non-image":
                    includeNonImage = true;
                    break;
                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;
                default:
                    return Fail($"Unknown argument: {argument}", out options, out error);
            }
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Fail("The output path cannot be empty.", out options, out error);
        }

        options = new RecorderOptions(
            Path.GetFullPath(outputPath),
            captureCount,
            TimeSpan.FromSeconds(timeoutSeconds),
            Array.AsReadOnly(labels),
            selfTest,
            prepareSamples,
            includeNonImage,
            showHelp);
        error = null;
        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        out string value)
    {
        if (index + 1 >= arguments.Count)
        {
            value = string.Empty;
            return false;
        }

        value = arguments[++index];
        return true;
    }

    private static bool Fail(
        string message,
        out RecorderOptions options,
        out string? error)
    {
        options = null!;
        error = message;
        return false;
    }

    private static string DefaultOutputPath() => Path.Combine(
        Path.GetTempPath(),
        "Vok.ClipboardImageRecorder",
        $"m2.3-image-formats-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
}
