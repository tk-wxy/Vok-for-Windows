using System.Security.Cryptography;

namespace Vok.StageDragProbe;

internal sealed record ProbeProcessIdentity(
    int ReportVersion,
    string EvidenceScope,
    string RunId,
    string Mode,
    string SourceRevision,
    int ProcessId,
    int OwnerThreadId,
    DateTimeOffset StartedUtc,
    string ExecutablePath,
    string ExecutableSha256,
    string AssemblyPath,
    string AssemblySha256)
{
    internal static ProbeProcessIdentity Capture(StageDragProbeOptions options)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable identity.");
        var assembly = typeof(ProbeProcessIdentity).Assembly.Location;
        return new(1, "ProcessLifecycle", Guid.NewGuid().ToString("N"), options.Verb.ToString(),
            options.SourceRevision, Environment.ProcessId, Environment.CurrentManagedThreadId,
            DateTimeOffset.UtcNow, executable, Hash(executable), assembly, Hash(assembly));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed record ProbePrepareReport(
    int ReportVersion, string EvidenceScope, string RunId, string SourceRevision,
    int ProcessId, string Status, string Profile, int SourceCount, int OccurrenceCount,
    string SourceTreeDigest, string OrderedOccurrenceDigest, DateTimeOffset CompletedUtc);

internal sealed record ProbeCommandFailure(
    int ReportVersion, string EvidenceScope, string RunId, string Mode, int ProcessId,
    string Status, string Stage, ProbeReceiverError Error, DateTimeOffset CompletedUtc);

internal sealed record ProbeProcessExitReport(
    int ReportVersion, string EvidenceScope, string RunId, string Mode, int ProcessId,
    int ExitCode, string Status, string Stage, ProbeReceiverError? Error,
    ProbeReceiverError? EvidenceError, DateTimeOffset CompletedUtc);
