namespace Vok.StageDragProbe;

internal static class ProbeCommandRunner
{
    // Narrow composition seams allow deterministic faults without diagnostic CLI
    // switches, product services, or source OLE in the executable.
    internal static int Run(
        StageDragProbeOptions options,
        ProbeOutputDirectory output,
        ProbeProcessIdentity identity,
        Action<ProbeFileDropReceiver>? runReceiver = null,
        Func<StageDragProbeOptions, ProbeOutputDirectory, string, ProbeFixtureBundle>? prepare = null,
        Func<ProbeOutputDirectory, ProbeTerminalReportWriter, IProbeReceiverRecorder>? recorderFactory = null)
    {
        var terminal = new ProbeTerminalReportWriter(output);
        ProbeEventLog? events = null;
        IProbeReceiverRecorder? unownedRecorder = null;
        ProbeFileDropReceiver? receiver = null;
        ProbeReceiverError? error = null;
        var stage = "OutputReserved";
        var prepared = false;

        void Capture(Exception exception)
        {
            error ??= ProbeReceiverError.From(exception);
            Console.Error.WriteLine(exception);
        }

        try
        {
            ProbeReportWriter.PublishNew(output, "startup.json", identity);
            if (options.Verb is StageDragProbeVerb.Prepare)
            {
                events = ProbeEventLog.Create(output);
                events.Write("ProcessStarted", identity);
                events.Write("OutputReserved", new { identity.RunId, output.Path });
                stage = "FixtureCreation";
                var bundle = (prepare ?? ProbeFixtureBuilder.Build)(options, output, identity.RunId);
                events.Write("FixtureCreated", new
                {
                    identity.RunId, bundle.Manifest.SourceTreeDigest, bundle.Manifest.OrderedOccurrenceDigest,
                    sourceCount = bundle.Manifest.Sources.Count, occurrenceCount = bundle.Manifest.Occurrences.Count,
                });
                stage = "PrepareReport";
                events.Write("PrepareTerminal", new { identity.RunId, status = "Prepared" });
                terminal.Complete(false, new ProbePrepareReport(
                    1, "FixturePreparation", identity.RunId, options.SourceRevision, identity.ProcessId,
                    "Prepared", bundle.Manifest.Profile, bundle.Manifest.Sources.Count,
                    bundle.Manifest.Occurrences.Count, bundle.Manifest.SourceTreeDigest,
                    bundle.Manifest.OrderedOccurrenceDigest, DateTimeOffset.UtcNow));
                prepared = true;
            }
            else
            {
                stage = "ReceiverCreation";
                unownedRecorder = (recorderFactory ?? CreateRecorder)(output, terminal);
                receiver = new ProbeFileDropReceiver(options.Effect!.Value, options.SourceRevision,
                    unownedRecorder, identity.RunId);
                unownedRecorder = null; // Receiver now owns disposal, including constructor-recorded failure.
                stage = "ReceiverLoop";
                if (receiver.Report is null) (runReceiver ?? ProbeReceiverProcessHost.Run)(receiver);
            }
        }
        catch (Exception exception)
        {
            Capture(exception);
            receiver?.FailHost(exception);
            try
            {
                events?.Write("ProcessFailed", new { identity.RunId, stage, error });
            }
            catch (Exception eventError) { Capture(eventError); }

            if (!terminal.Attempted)
            {
                try
                {
                    terminal.Complete(true, new ProbeCommandFailure(
                        1, "ProcessLifecycle", identity.RunId, identity.Mode, identity.ProcessId,
                        "Failed", stage, ProbeReceiverError.From(exception), DateTimeOffset.UtcNow));
                }
                catch (Exception reportError) { Capture(reportError); }
            }
        }
        finally
        {
            try { receiver?.Dispose(); }
            catch (Exception exception) { Capture(exception); }
            try { unownedRecorder?.Dispose(); }
            catch (Exception exception) { Capture(exception); }
            try { events?.Dispose(); }
            catch (Exception exception) { Capture(exception); }
        }

        var evidenceError = receiver?.EvidenceError;
        var exitCode = error is not null || evidenceError is not null ? 1
            : prepared ? 0 : ReceiverExitCode(receiver);
        var status = exitCode == 1 ? "Failed" : prepared ? "Prepared" : receiver!.Report!.Status;
        try
        {
            // Separate process/cleanup evidence; never rewrites the receiver's OLE observation.
            ProbeReportWriter.PublishNew(output, "exit.json", new ProbeProcessExitReport(
                1, "ProcessLifecycle", identity.RunId, identity.Mode, identity.ProcessId,
                exitCode, status, stage, error, evidenceError, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            Capture(exception);
            return 1;
        }

        if (evidenceError is not null) Console.Error.WriteLine($"Evidence failure: {evidenceError}");
        Console.WriteLine($"{identity.Mode}: {status}; run={identity.RunId}; exit={exitCode}");
        return exitCode;
    }

    private static int ReceiverExitCode(ProbeFileDropReceiver? receiver) => receiver?.Report?.Status switch
    {
        "Accepted" => 0,
        "Rejected" or "Cancelled" => 2,
        _ => 1,
    };

    private static IProbeReceiverRecorder CreateRecorder(ProbeOutputDirectory output, ProbeTerminalReportWriter terminal) =>
        new ProbeReceiverReportRecorder(output, terminal);
}
