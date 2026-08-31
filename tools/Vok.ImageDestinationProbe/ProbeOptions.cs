using System.Globalization;

namespace Vok.ImageDestinationProbe;

internal sealed record ProbeOptions(
    bool SelfTest,
    bool Enumerate,
    int Count,
    int TimeoutSeconds,
    string? ExpectedPath,
    bool IncludePaths)
{
    public static ProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var selfTest = false;
        var enumerate = false;
        var count = 1;
        var timeoutSeconds = 300;
        string? expectedPath = null;
        var includePaths = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--self-test":
                    selfTest = true;
                    break;
                case "--enumerate":
                    enumerate = true;
                    break;
                case "--include-paths":
                    includePaths = true;
                    break;
                case "--count":
                    count = ParseInt(NextValue(args, ref index, "--count"), "--count", 1, 100);
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = ParseInt(
                        NextValue(args, ref index, "--timeout-seconds"),
                        "--timeout-seconds",
                        1,
                        3600);
                    break;
                case "--expected-path":
                    expectedPath = NextValue(args, ref index, "--expected-path");
                    break;
                default:
                    throw new ArgumentException($"Unknown probe argument: {args[index]}");
            }
        }

        if (selfTest && enumerate)
        {
            throw new ArgumentException("--self-test and --enumerate cannot be combined.");
        }

        if (!string.IsNullOrWhiteSpace(expectedPath))
        {
            expectedPath = NormalizePath(expectedPath);
        }

        return new ProbeOptions(
            selfTest,
            enumerate,
            count,
            timeoutSeconds,
            expectedPath,
            includePaths);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int ParseInt(
        string value,
        string option,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                option,
                $"{option} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    internal static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
