using System.Text.Json;
using System.Threading.Channels;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Input;
using Vok.Infrastructure.Windows.Launcher;
using Vok.Infrastructure.Windows.Windowing;

namespace Vok.ImageDestinationProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var shellExecutor = new WindowsShellStaExecutor();
            if (options.SelfTest)
            {
                var report = await ShellFileOperationProbe.RunAsync(
                    shellExecutor,
                    timeout.Token).ConfigureAwait(false);
                WriteJson(report);
                return report.Success ? 0 : 1;
            }

            if (options.Enumerate)
            {
                var report = await shellExecutor.ExecuteAsync(
                    () => ShellDestinationInspector.Enumerate(options.IncludePaths),
                    timeout.Token).ConfigureAwait(false);
                WriteJson(report);
                return report.Success ? 0 : 1;
            }

            return await RunInteractiveAsync(
                options,
                shellExecutor,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            WriteJson(new
            {
                reportVersion = 1,
                success = false,
                errorCode = "TimeoutOrCancellation",
            });
            return 2;
        }
        catch (Exception exception)
        {
            WriteJson(new
            {
                reportVersion = 1,
                success = false,
                errorCode = $"{exception.GetType().Name}:0x{exception.HResult:X8}",
            });
            return 1;
        }
    }

    private static async Task<int> RunInteractiveAsync(
        ProbeOptions options,
        WindowsShellStaExecutor shellExecutor,
        CancellationToken cancellationToken)
    {
        var captures = Channel.CreateBounded<ForegroundTarget>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
        });
        var fault = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gesture = new GlobalHotkeyGesture(
            GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift,
            0x79); // F10
        using var hotkey = new GlobalHotkeyService(
            new Win32ForegroundWindowService(),
            gesture);
        hotkey.PressChanged += (_, press) =>
        {
            if (press is HotkeyPressStarted started)
            {
                _ = captures.Writer.TryWrite(started.ForegroundTarget);
            }
        };
        hotkey.Faulted += (_, exception) => fault.TrySetResult(exception);
        await hotkey.StartAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"Focus an Explorer folder or Desktop and press {gesture.DisplayName}. " +
            $"Captures remaining: {options.Count}.");
        var reports = new List<DestinationInspectionReport>(options.Count);
        for (var index = 0; index < options.Count; index++)
        {
            var read = captures.Reader.ReadAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(read, fault.Task).ConfigureAwait(false);
            if (completed == fault.Task)
            {
                var exception = await fault.Task.ConfigureAwait(false);
                throw new InvalidOperationException("The probe hotkey failed.", exception);
            }

            var target = await read.ConfigureAwait(false);
            var report = await shellExecutor.ExecuteAsync(
                    () => ShellDestinationInspector.Inspect(
                        target,
                        options.ExpectedPath,
                        options.IncludePaths),
                    cancellationToken)
                .ConfigureAwait(false);
            reports.Add(report);
            WriteJson(report);
            if (index + 1 < options.Count)
            {
                Console.WriteLine(
                    $"Capture {index + 1}/{options.Count} complete. " +
                    $"Focus the next target and press {gesture.DisplayName}.");
            }
        }

        var success = reports.All(static report => report.Success) &&
            (options.ExpectedPath is null ||
             reports.All(static report => report.ExpectedPathMatches is true));
        return success ? 0 : 1;
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
