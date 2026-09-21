namespace Vok.StageDragProbe;

internal enum StageDragProbeVerb
{
    Prepare,
    Receive,
}

internal enum ProbeFixtureProfile
{
    Single,
    Batch10,
    Batch100,
    Mixed,
    Duplicate,
}

internal enum ProbeReceiverEffect
{
    Copy,
    None,
    Throw,
}

internal sealed record StageDragProbeOptions(
    StageDragProbeVerb Verb,
    string AllowedRoot,
    string OutputRoot,
    string RepositoryRoot,
    string SourceRevision,
    ProbeFixtureProfile? Profile,
    ProbeReceiverEffect? Effect)
{
    internal static StageDragProbeOptions Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0)
        {
            throw new ArgumentException(
                "Expected prepare|receive followed by explicit named options.");
        }

        var verb = arguments[0].ToLowerInvariant() switch
        {
            "prepare" => StageDragProbeVerb.Prepare,
            "receive" => StageDragProbeVerb.Receive,
            _ => throw new ArgumentException($"Unknown probe verb: {arguments[0]}"),
        };
        var values = ParseNamedArguments(arguments.AsSpan(1));
        EnsureKnownArguments(values.Keys);

        var allowedRoot = GetRequiredAbsolutePath(values, "--allowed-root");
        var outputRoot = GetRequiredAbsolutePath(values, "--output-root");
        var repositoryRoot = GetRequiredAbsolutePath(values, "--repository-root");
        var sourceRevision = GetRequired(values, "--source-revision").Trim();
        if (sourceRevision.Length > 128 || sourceRevision.Any(char.IsControl))
        {
            throw new ArgumentException(
                "--source-revision must be at most 128 non-control characters.");
        }

        ProbeFixtureProfile? profile = null;
        ProbeReceiverEffect? effect = null;
        if (verb is StageDragProbeVerb.Prepare)
        {
            if (values.ContainsKey("--effect"))
            {
                throw new ArgumentException("--effect is valid only for receive.");
            }

            profile = ParseProfile(GetRequired(values, "--profile"));
        }
        else
        {
            if (values.ContainsKey("--profile"))
            {
                throw new ArgumentException("--profile is valid only for prepare.");
            }

            effect = ParseEffect(GetRequired(values, "--effect"));
        }

        return new StageDragProbeOptions(
            verb,
            allowedRoot,
            outputRoot,
            repositoryRoot,
            sourceRevision,
            profile,
            effect);
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
            "--output-root",
            "--repository-root",
            "--source-revision",
            "--profile",
            "--effect",
        };
        foreach (var name in names)
        {
            if (!known.Contains(name))
            {
                throw new ArgumentException($"Unknown probe option: {name}");
            }
        }
    }

    private static string GetRequiredAbsolutePath(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        var value = GetRequired(values, name);
        if (!Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{name} must be a fully qualified local path.");
        }

        return NormalizePath(Path.GetFullPath(value));
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required probe option: {name}");
        }

        return value;
    }

    private static ProbeFixtureProfile ParseProfile(string value) =>
        value.ToLowerInvariant() switch
        {
            "single" => ProbeFixtureProfile.Single,
            "batch10" => ProbeFixtureProfile.Batch10,
            "batch100" => ProbeFixtureProfile.Batch100,
            "mixed" => ProbeFixtureProfile.Mixed,
            "duplicate" => ProbeFixtureProfile.Duplicate,
            _ => throw new ArgumentException($"Unknown fixture profile: {value}"),
        };

    private static ProbeReceiverEffect ParseEffect(string value) =>
        value.ToLowerInvariant() switch
        {
            "copy" => ProbeReceiverEffect.Copy,
            "none" => ProbeReceiverEffect.None,
            "throw" => ProbeReceiverEffect.Throw,
            _ => throw new ArgumentException($"Unknown receiver effect: {value}"),
        };

    internal static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
