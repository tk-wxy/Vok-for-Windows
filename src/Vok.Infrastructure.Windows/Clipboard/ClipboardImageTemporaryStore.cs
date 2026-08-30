using System.IO;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardImageTemporaryStore : IDisposable
{
    private static readonly string[] OwnedPatterns =
    [
        "normalized-*.png",
        "thumbnail-*.png",
        "replay-*.dibv5",
        "encoding-*.tmp",
    ];

    private readonly object _gate = new();
    private readonly string _rootPath;
    private readonly Action<Exception>? _cleanupFault;
    private readonly HashSet<string> _outstanding =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ClipboardImageTemporaryStore(
        string rootPath,
        Action<Exception>? cleanupFault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _cleanupFault = cleanupFault;
        Directory.CreateDirectory(_rootPath);
        CleanupStaleFiles();
    }

    public static ClipboardImageTemporaryStore CreateDefault(
        Action<Exception>? cleanupFault = null)
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return new ClipboardImageTemporaryStore(
            Path.Combine(localData, "Vok", "assets", "clipboard", ".staging"),
            cleanupFault);
    }

    public int OutstandingLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _outstanding.Count;
            }
        }
    }

    public IClipboardPayloadLease CreatePng(
        string role,
        Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(write);
        if (role is not ("normalized" or "thumbnail"))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Path.Combine(_rootPath, $"{role}-{Guid.NewGuid():N}.png");
        var encodingPath = Path.Combine(
            _rootPath,
            $"encoding-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       encodingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                write(stream);
                stream.Flush(flushToDisk: false);
            }

            using (var input = new FileStream(
                       encodingPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                CanonicalPngChunkFilter.CopyPixelChunks(input, output);
                output.Flush(flushToDisk: false);
            }

            var length = new FileInfo(path).Length;
            if (length <= 0)
            {
                throw new InvalidDataException("The encoded PNG is empty.");
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _outstanding.Add(path);
            }

            return new TemporaryBlobLease(this, path, length);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            TryDelete(encodingPath);
        }
    }

    public IClipboardPayloadLease CreateDibV5(
        long expectedLength,
        Action<Stream> write)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedLength, 125);
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Path.Combine(
            _rootPath,
            $"replay-{Guid.NewGuid():N}.dibv5");
        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                write(stream);
                if (stream.Position != expectedLength)
                {
                    throw new InvalidDataException(
                        "The replay DIBV5 length does not match its declared size.");
                }

                stream.Flush(flushToDisk: false);
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _outstanding.Add(path);
            }

            return new TemporaryBlobLease(this, path, expectedLength);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public void Dispose()
    {
        string[] outstanding;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            outstanding = _outstanding.ToArray();
            _outstanding.Clear();
        }

        foreach (var path in outstanding)
        {
            TryDelete(path);
        }
    }

    private Stream OpenRead(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_outstanding.Contains(path))
            {
                throw new ObjectDisposedException(nameof(TemporaryBlobLease));
            }
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            _outstanding.Remove(path);
        }

        TryDelete(path);
    }

    private void CleanupStaleFiles()
    {
        foreach (var pattern in OwnedPatterns)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             _rootPath,
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    TryDelete(path);
                }
            }
            catch (Exception exception)
            {
                ReportCleanupFault(new IOException(
                    $"Unable to enumerate clipboard image staging files in '{_rootPath}'.",
                    exception));
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            ReportCleanupFault(new IOException(
                "Unable to delete a normalized clipboard image payload.",
                exception));
        }
    }

    private void ReportCleanupFault(Exception exception)
    {
        try
        {
            _cleanupFault?.Invoke(exception);
        }
        catch
        {
            // Diagnostic reporting must not strand other temporary files.
        }
    }

    private sealed class TemporaryBlobLease(
        ClipboardImageTemporaryStore owner,
        string path,
        long length) : IClipboardPayloadLease
    {
        private ClipboardImageTemporaryStore? _owner = owner;

        public long Length { get; } = length;

        public Stream OpenRead()
        {
            var owner = Volatile.Read(ref _owner);
            ObjectDisposedException.ThrowIf(owner is null, this);
            return owner.OpenRead(path);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(path);
    }
}
