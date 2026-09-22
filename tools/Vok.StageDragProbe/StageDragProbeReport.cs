using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vok.StageDragProbe;

internal static class ProbeReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string PublishNew<T>(ProbeOutputDirectory output, string fileName, T value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var final = ResolveOutputFile(output, fileName);
        var pending = WriteNew(output, $"{fileName}.{Guid.NewGuid():N}.pending", value);
        File.Move(pending, final, overwrite: false);
        return final;
    }

    internal static string WriteNew<T>(
        ProbeOutputDirectory output,
        string fileName,
        T value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var path = ResolveOutputFile(output, fileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    internal static T Read<T>(string path) where T : notnull
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options) ??
            throw new InvalidDataException($"The JSON file was empty: {path}");
    }

    internal static string ResolveOutputFile(
        ProbeOutputDirectory output,
        string fileName)
    {
        if (Path.IsPathFullyQualified(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName is "." or "..")
        {
            throw new ArgumentException(
                "A probe output file name must be a single relative file name.",
                nameof(fileName));
        }

        if (!Directory.Exists(output.Path))
        {
            throw new DirectoryNotFoundException(
                $"The reserved output root does not exist: {output.Path}");
        }

        return Path.Combine(output.Path, fileName);
    }
}

// Shared by the command boundary and receiver: even failure recovery gets only
// one publication attempt. Failed staging remains as evidence, never overwritten.
internal sealed class ProbeTerminalReportWriter
{
    private readonly ProbeOutputDirectory _output;

    internal ProbeTerminalReportWriter(ProbeOutputDirectory output)
    {
        _output = output;
        EnsureNoTerminalFile();
    }

    internal bool Attempted { get; private set; }

    internal void Complete<T>(bool failed, T report)
    {
        if (Attempted) throw new InvalidOperationException("A terminal report has already been attempted.");
        Attempted = true;
        EnsureNoTerminalFile();
        ProbeReportWriter.PublishNew(_output, failed ? "failure.json" : "report.json", report);
    }

    private void EnsureNoTerminalFile()
    {
        foreach (var name in new[] { "report.json", "failure.json" })
        {
            var path = ProbeReportWriter.ResolveOutputFile(_output, name);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"Probe evidence already exists: {path}");
        }
    }
}

internal sealed class ProbeEventLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FileStream _stream;
    private readonly object _gate = new();
    private long _sequence;
    private bool _disposed;

    private ProbeEventLog(FileStream stream)
    {
        _stream = stream;
    }

    internal static ProbeEventLog Create(ProbeOutputDirectory output)
    {
        var path = ProbeReportWriter.ResolveOutputFile(output, "events.jsonl");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        return new ProbeEventLog(stream);
    }

    internal void Write(string name)
    {
        Write<object?>(name, null);
    }

    internal void Write<T>(string name, T? details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    eventVersion = 1,
                    sequence = checked(++_sequence),
                    timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                    name,
                    details,
                },
                Options);
            _stream.Write(bytes);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
        }
    }
}
