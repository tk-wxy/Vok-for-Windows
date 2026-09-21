using System.Windows;

namespace Vok.StageScaleProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        ProbeOptions? options = null;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            options = ProbeOptions.Parse(arguments);
            return options.Verb switch
            {
                ProbeVerb.Prepare => RunPrepare(options, cancellation.Token),
                ProbeVerb.Run => RunWpf(options, cancellation.Token),
                _ => throw new InvalidOperationException(
                    $"Unsupported probe verb: {options.Verb}"),
            };
        }
        catch (Exception exception)
        {
            TryWriteFailure(options, exception);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunPrepare(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var report = StageScaleData
            .PrepareAsync(options, cancellationToken)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(
            $"prepared {report.RequestedItemCount} items in " +
            $"{report.SeedElapsedMilliseconds:F1} ms: {report.Root}");
        return 0;
    }

    private static int RunWpf(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var marker = StageScaleData.ValidatePreparedFixture(options);
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        Exception? failure = null;
        var exitCode = 1;
        application.Startup += async (_, _) =>
        {
            try
            {
                var runner = new StageScaleRunner(application, options, marker);
                var report = await runner
                    .RunAsync(cancellationToken)
                    .ConfigureAwait(true);
                ProbeReportWriter.WriteNew(options.ReportPath, report);
                Console.WriteLine(
                    $"run {report.RequestedItemCount}: " +
                    $"edges={report.Traversal.CompletedEdgeSteps}; " +
                    $"visited={report.Traversal.VisitedOccurrenceCount}; " +
                    $"anchorMax={report.Traversal.MaximumAnchorErrorDip:F3} DIP");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                failure = exception;
                TryWriteFailure(options, exception);
            }
            finally
            {
                application.Shutdown(exitCode);
            }
        };
        application.Run();
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
        }

        return exitCode;
    }

    private static void TryWriteFailure(ProbeOptions? options, Exception exception)
    {
        if (options is null || File.Exists(options.ReportPath))
        {
            return;
        }

        var parent = Path.GetDirectoryName(options.ReportPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return;
        }

        try
        {
            ProbeReportWriter.WriteNew(
                options.ReportPath,
                new FailureProbeReport(
                    ReportVersion: 1,
                    Status: "Failed",
                    Mode: options.Verb.ToString(),
                    FailedUtc: DateTimeOffset.UtcNow.ToString("O"),
                    SourceRevision: options.SourceRevision,
                    ErrorType: exception.GetType().FullName ?? exception.GetType().Name,
                    ErrorMessage: exception.Message,
                    StackTrace: exception.StackTrace));
        }
        catch
        {
            // Preserve the original probe failure; reporting is best effort.
        }
    }
}
