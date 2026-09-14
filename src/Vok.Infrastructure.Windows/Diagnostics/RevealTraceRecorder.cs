using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Vok.Core.Diagnostics;

namespace Vok.Infrastructure.Windows.Diagnostics;

/// <summary>One optional owner for diagnostic I/O and system-event subscriptions.</summary>
public sealed partial class RevealTraceRecorder : IDisposable
{
    public const string DirectoryEnvironmentVariable = "VOK_REVEAL_TRACE_DIRECTORY";
    private readonly RevealTrace _trace;
    private readonly BlockingCollection<byte> _requests = new(1);
    private readonly Thread _thread;
    private int _disposed;
    private bool _powerSubscribed;
    private bool _sessionSubscribed;
    private long _exports;
    private string? _lastError;

    public RevealTraceRecorder(RevealTrace trace, string directory)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _trace = trace;
        Directory.CreateDirectory(directory);
        OutputPath = Path.Combine(Path.GetFullPath(directory),
            $"reveal-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        _thread = new Thread(Run) { IsBackground = true, Name = "Vok.RevealTrace" };
        _trace.ExportRequested += RequestExport;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerSubscribed = true;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _sessionSubscribed = true;
        }
        catch (Exception exception)
        {
            _trace.Record("RecoveryObservationUnavailable", detail: exception.GetType().Name);
        }
        try { _thread.Start(); }
        catch
        {
            _trace.ExportRequested -= RequestExport;
            if (_powerSubscribed) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (_sessionSubscribed) SystemEvents.SessionSwitch -= OnSessionSwitch;
            _requests.Dispose();
            throw;
        }
        RequestExport();
    }

    public string OutputPath { get; }
    public long ExportCount => Interlocked.Read(ref _exports);
    public string? LastError => Volatile.Read(ref _lastError);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _trace.Record("PowerMode", detail: e.Mode.ToString());
        RequestExport();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _trace.Record("SessionSwitch", detail: e.Reason.ToString());
        RequestExport();
    }

    private void RequestExport()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _requests.TryAdd(0); }
        catch (InvalidOperationException) { /* Dispose owns completion. */ }
    }

    private void Run()
    {
        foreach (var _ in _requests.GetConsumingEnumerable()) Export();
        Export(); // Preserve final failure/hide events even if they coalesced with shutdown.
        _requests.Dispose();
    }

    private void Export()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            var hasCounters = GetProcessMemoryInfo(process.Handle, ref counters, counters.Size);
            _trace.Record("ResourceSample", value1: process.WorkingSet64,
                value2: process.PrivateMemorySize64,
                detail: $"pageFaults={(hasCounters ? counters.PageFaultCount : null)};" +
                    $"gc={GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)};" +
                    $"handles={process.HandleCount};threads={process.Threads.Count}");
            var snapshot = _trace.Capture();
            // A unique candidate file and one replaceable sibling bound storage use.
            var temporaryPath = OutputPath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                JsonSerializer.Serialize(stream, snapshot);
            File.Move(temporaryPath, OutputPath, overwrite: true);
            Interlocked.Increment(ref _exports);
            Volatile.Write(ref _lastError, null);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastError, exception.GetType().Name);
            _trace.Record("TraceExportFailed", detail: exception.GetType().Name);
            Trace.TraceWarning("Reveal diagnostics export failed: {0}.", exception.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _trace.ExportRequested -= RequestExport;
        if (_powerSubscribed) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_sessionSubscribed) SystemEvents.SessionSwitch -= OnSessionSwitch;
        _requests.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            Trace.TraceWarning("Reveal diagnostic writer is still finishing after its shutdown watchdog.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(
        nint process, ref ProcessMemoryCounters counters, uint size);
}
