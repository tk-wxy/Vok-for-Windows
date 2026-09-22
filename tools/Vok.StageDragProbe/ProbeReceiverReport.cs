namespace Vok.StageDragProbe;

internal sealed record ProbeReceiverError(string Type, string Message)
{
    internal static ProbeReceiverError From(Exception exception) =>
        new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
}

internal sealed record ProbeReceiverReport(
    int ReportVersion,
    string EvidenceScope,
    string RunId,
    long Generation,
    string SourceRevision,
    string Policy,
    int ProcessId,
    int OwnerThreadId,
    long WindowHandle,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string Status,
    string Reason,
    int RawEffect,
    int FirstAllowedEffects,
    int LastAllowedEffects,
    int EnterCount,
    int OverCount,
    int DropCount,
    int FileDropReadCount,
    IReadOnlyList<string> NativeFormats,
    IReadOnlyList<string> AllFormats,
    IReadOnlyList<string> Paths,
    ProbeReceiverError? Error,
    IReadOnlyList<byte>? PreferredDropEffectBytes = null,
    int PreferredDropEffectReadCount = 0);

internal interface IProbeReceiverRecorder : IDisposable
{
    void Record(string name, object details);

    void Complete(ProbeReceiverReport report);
}

// One recorder owns one reserved output and one terminal publication attempt.
internal sealed class ProbeReceiverReportRecorder : IProbeReceiverRecorder
{
    private readonly ProbeTerminalReportWriter _terminal;
    private readonly ProbeEventLog _events;

    internal ProbeReceiverReportRecorder(
        ProbeOutputDirectory output, ProbeTerminalReportWriter? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _terminal = terminal ?? new ProbeTerminalReportWriter(output);
        _events = ProbeEventLog.Create(output);
    }

    public void Record(string name, object details) => _events.Write(name, details);

    public void Complete(ProbeReceiverReport report) =>
        _terminal.Complete(report.Status is "Failed" or "Cancelled", report);

    public void Dispose() => _events.Dispose();
}
