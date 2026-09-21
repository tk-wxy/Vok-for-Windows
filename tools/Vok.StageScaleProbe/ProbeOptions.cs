using System.Globalization;

namespace Vok.StageScaleProbe;

internal enum ProbeVerb
{
    Prepare,
    Run,
}

internal sealed record ProbeOptions(
    ProbeVerb Verb,
    string AllowedRoot,
    string Root,
    string ReportPath,
    string SourceRevision,
    int ItemCount,
    int EdgeSteps,
    double WindowWidth,
    double WindowHeight)
{
    internal const int MaximumItemCount = 100_000;
    internal const int MaximumEdgeSteps = 5_000;

    internal string DatabasePath => Path.Combine(Root, "scale.db");

    internal string SourcePath => Path.Combine(Root, "source.txt");

    internal string MarkerPath => Path.Combine(Root, "probe.json");

    internal static ProbeOptions Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0)
        {
            throw new ArgumentException(
                "Expected: prepare|run --allowed-root PATH --root PATH " +
                "--report PATH --source-revision VALUE --count VALUE.");
        }

        var verb = arguments[0].ToLowerInvariant() switch
        {
            "prepare" => ProbeVerb.Prepare,
            "run" => ProbeVerb.Run,
            _ => throw new ArgumentException($"Unknown probe verb: {arguments[0]}"),
        };
        var values = ParseNamedArguments(arguments.AsSpan(1));
        EnsureKnownArguments(values.Keys);

        var allowedRoot = GetRequiredPath(values, "--allowed-root");
        var root = GetRequiredPath(values, "--root");
        var reportPath = GetRequiredPath(values, "--report");
        var sourceRevision = GetRequired(values, "--source-revision");
        var itemCount = ParseInt32(
            GetRequired(values, "--count"),
            "--count",
            1,
            MaximumItemCount);
        var edgeSteps = ParseInt32(
            GetOptional(values, "--edge-steps", "0"),
            "--edge-steps",
            0,
            MaximumEdgeSteps);
        var windowWidth = ParseDouble(
            GetOptional(values, "--window-width", "1500"),
            "--window-width",
            640,
            3840);
        var windowHeight = ParseDouble(
            GetOptional(values, "--window-height", "900"),
            "--window-height",
            480,
            2160);

        var options = new ProbeOptions(
            verb,
            allowedRoot,
            root,
            reportPath,
            sourceRevision,
            itemCount,
            edgeSteps,
            windowWidth,
            windowHeight);
        ProbePaths.Validate(options);
        return options;
    }

    private static Dictionary<string, string> ParseNamedArguments(
        ReadOnlySpan<string> arguments)
    {
        if ((arguments.Length & 1) != 0)
        {
            throw new ArgumentException("Every probe option requires one value.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var name = arguments[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException($"Invalid or duplicate probe option: {name}");
            }
        }

        return values;
    }

    private static void EnsureKnownArguments(IEnumerable<string> names)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--allowed-root",
            "--root",
            "--report",
            "--source-revision",
            "--count",
            "--edge-steps",
            "--window-width",
            "--window-height",
        };
        foreach (var name in names)
        {
            if (!known.Contains(name))
            {
                throw new ArgumentException($"Unknown probe option: {name}");
            }
        }
    }

    private static string GetRequiredPath(
        IReadOnlyDictionary<string, string> values,
        string name) => Path.GetFullPath(GetRequired(values, name));

    private static string GetRequired(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required probe option: {name}");
        }

        return value;
    }

    private static string GetOptional(
        IReadOnlyDictionary<string, string> values,
        string name,
        string fallback) => values.TryGetValue(name, out var value) ? value : fallback;

    private static int ParseInt32(string value, string name, int minimum, int maximum)
    {
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result) ||
            result < minimum || result > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Expected an integer from {minimum} through {maximum}.");
        }

        return result;
    }

    private static double ParseDouble(
        string value,
        string name,
        double minimum,
        double maximum)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result) ||
            !double.IsFinite(result) || result < minimum || result > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Expected a finite value from {minimum} through {maximum}.");
        }

        return result;
    }
}

internal static class ProbePaths
{
    internal static void Validate(ProbeOptions options)
    {
        if (!Directory.Exists(options.AllowedRoot))
        {
            throw new DirectoryNotFoundException(
                $"The allowed output root does not exist: {options.AllowedRoot}");
        }

        EnsureStrictDescendant(options.Root, options.AllowedRoot, "probe root");
        EnsureDescendantOrEqual(options.ReportPath, options.Root, "report path");

        if (options.Verb is ProbeVerb.Prepare)
        {
            if (Directory.Exists(options.Root) || File.Exists(options.Root))
            {
                throw new IOException(
                    $"The prepare target already exists: {options.Root}");
            }
        }
        else if (!Directory.Exists(options.Root))
        {
            throw new DirectoryNotFoundException(
                $"The prepared probe root does not exist: {options.Root}");
        }
    }

    internal static void EnsureStrictDescendant(
        string candidate,
        string root,
        string description)
    {
        EnsureDescendantOrEqual(candidate, root, description);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(candidate),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {description} must be a child of its allowed root.");
        }
    }

    internal static void EnsureDescendantOrEqual(
        string candidate,
        string root,
        string description)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {description} is outside the allowed root: {candidate}");
        }
    }
}
