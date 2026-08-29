using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class ClipboardStagingStore : IDisposable
{
    private const string StagingPattern = "capture-*.blob";
    private readonly object _gate = new();
    private readonly string _rootPath;
    private readonly Action<Exception>? _cleanupFault;
    private readonly Dictionary<string, long> _outstanding =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ClipboardStagingStore(
        string rootPath,
        Action<Exception>? cleanupFault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _cleanupFault = cleanupFault;
        Directory.CreateDirectory(_rootPath);
        CleanupStaleFiles();
    }

    public static ClipboardStagingStore CreateDefault(
        Action<Exception>? cleanupFault = null)
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return new ClipboardStagingStore(
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

    public long OutstandingByteLength
    {
        get
        {
            lock (_gate)
            {
                return _outstanding.Values.Sum();
            }
        }
    }

    public IClipboardPayloadLease CopyFromLockedMemory(
        nint source,
        int byteLength,
        int copyBufferBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfEqual(source, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(copyBufferBytes, 1);

        var path = Path.Combine(
            _rootPath,
            $"capture-{Guid.NewGuid():N}.blob");
        var buffer = ArrayPool<byte>.Shared.Rent(copyBufferBytes);
        try
        {
            using (var output = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       copyBufferBytes,
                       FileOptions.SequentialScan))
            {
                var copied = 0;
                while (copied < byteLength)
                {
                    var count = Math.Min(copyBufferBytes, byteLength - copied);
                    Marshal.Copy(source + copied, buffer, 0, count);
                    output.Write(buffer, 0, count);
                    copied += count;
                }
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _outstanding.Add(path, byteLength);
            }

            return new StagingBlobLease(this, path, byteLength);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        KeyValuePair<string, long>[] outstanding;
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

        foreach (var entry in outstanding)
        {
            TryDelete(entry.Key);
        }
    }

    private Stream OpenRead(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_outstanding.ContainsKey(path))
            {
                throw new ObjectDisposedException(nameof(StagingBlobLease));
            }
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
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
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _rootPath,
                         StagingPattern,
                         SearchOption.TopDirectoryOnly))
            {
                TryDelete(path);
            }
        }
        catch (Exception exception)
        {
            ReportCleanupFault(new IOException(
                $"Unable to enumerate clipboard staging files in '{_rootPath}'.",
                exception));
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
                "Unable to delete a clipboard staging payload.",
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
            // Cleanup reporting must not prevent the remaining leases from being released.
        }
    }

    private sealed class StagingBlobLease(
        ClipboardStagingStore owner,
        string path,
        long length) : IClipboardPayloadLease
    {
        private ClipboardStagingStore? _owner = owner;

        public long Length { get; } = length;

        public Stream OpenRead()
        {
            var currentOwner = Volatile.Read(ref _owner);
            ObjectDisposedException.ThrowIf(currentOwner is null, this);
            return currentOwner.OpenRead(path);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(path);
    }
}
