using System.IO;
using System.Text.Json;
using Vok.Core.Diagnostics;
using Vok.Infrastructure.Windows.Diagnostics;
using Xunit;

namespace Vok.IntegrationTests.Diagnostics;

public sealed class RevealTraceRecorderTests
{
    [Fact]
    public void ExportAndShutdownKeepBoundedEvidenceAndDetachFromTheProducer()
    {
        var directory = Directory.CreateTempSubdirectory("vok-reveal-trace-");
        try
        {
            var trace = new RevealTrace(true, 32);
            var recorder = new RevealTraceRecorder(trace, directory.FullName);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => recorder.ExportCount > 0, TimeSpan.FromSeconds(5)));
                for (var index = 0; index < 100; index++) trace.Record("Sample", generation: index);
                trace.Record("FinalFailure", generation: 101);
            }
            finally { recorder.Dispose(); }
            using var json = JsonDocument.Parse(File.ReadAllText(recorder.OutputPath));
            Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
            Assert.True(json.RootElement.GetProperty("OverwrittenEntries").GetInt64() > 0);
            Assert.True(json.RootElement.GetProperty("Entries").GetArrayLength() <= 32);
            Assert.Contains(json.RootElement.GetProperty("Entries").EnumerateArray(),
                entry => entry.GetProperty("Phase").GetString() == "FinalFailure");
            var exports = recorder.ExportCount;
            trace.RequestExport();
            recorder.Dispose();
            Assert.Equal(exports, recorder.ExportCount);
            Assert.Single(directory.GetFiles());
            Assert.Null(recorder.LastError);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void AnUnwritableOutputIsReportedAndDoesNotEscapeToTheUiProducer()
    {
        var directory = Directory.CreateTempSubdirectory("vok-reveal-failure-");
        try
        {
            var trace = new RevealTrace(true);
            using var recorder = new RevealTraceRecorder(trace, directory.FullName);
            Assert.True(SpinWait.SpinUntil(() => recorder.ExportCount > 0, TimeSpan.FromSeconds(5)));
            using (var lease = new FileStream(recorder.OutputPath, FileMode.Open,
                       FileAccess.Read, FileShare.None))
            {
                trace.Record("RevealFailed", generation: 7);
                trace.RequestExport();
                Assert.True(SpinWait.SpinUntil(() => recorder.LastError is not null, TimeSpan.FromSeconds(5)));
                Assert.Contains(trace.Capture().Entries, entry => entry.Phase == "TraceExportFailed");
            }
            trace.RequestExport();
            Assert.True(SpinWait.SpinUntil(() => recorder.LastError is null, TimeSpan.FromSeconds(5)));
        }
        finally { directory.Delete(recursive: true); }
    }
}
