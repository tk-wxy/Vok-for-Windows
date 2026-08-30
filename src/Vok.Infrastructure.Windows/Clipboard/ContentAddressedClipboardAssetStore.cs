using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class ContentAddressedClipboardAssetStore :
    IClipboardContentAssetStore
{
    private const string StagingPattern = "asset-*.tmp";
    private readonly string _rootPath;
    private readonly string _stagingPath;
    private readonly ClipboardImageCaptureOptions _options;
    private readonly Action<Exception>? _cleanupFault;
    private int _disposed;

    public ContentAddressedClipboardAssetStore(
        string rootPath,
        ClipboardImageCaptureOptions? options = null,
        Action<Exception>? cleanupFault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _stagingPath = Path.Combine(_rootPath, ".staging");
        _options = options ?? new ClipboardImageCaptureOptions();
        _cleanupFault = cleanupFault;
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_stagingPath);
        CleanupOwnedStagingFiles();
    }

    public static ContentAddressedClipboardAssetStore CreateDefault(
        ClipboardImageCaptureOptions? options = null,
        Action<Exception>? cleanupFault = null)
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return new ContentAddressedClipboardAssetStore(
            Path.Combine(localData, "Vok", "assets", "clipboard"),
            options,
            cleanupFault);
    }

    internal int OwnedStagingFileCount =>
        Directory.Exists(_stagingPath)
            ? Directory.EnumerateFiles(
                _stagingPath,
                StagingPattern,
                SearchOption.TopDirectoryOnly).Count()
            : 0;

    internal string RootPath => _rootPath;

    public ClipboardPreparedAsset CommitPng(
        Stream source,
        long expectedLength,
        int expectedPixelWidth,
        int expectedPixelHeight,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead ||
            expectedLength is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumInputBytes ||
            expectedPixelWidth < 1 ||
            expectedPixelHeight < 1)
        {
            throw new ArgumentException(
                "The PNG source metadata is invalid.",
                nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stagingPath = Path.Combine(
            _stagingPath,
            $"asset-{Guid.NewGuid():N}.tmp");
        try
        {
            WriteStagingFile(source, stagingPath, expectedLength, cancellationToken);
            var hash = VerifyPngFile(
                stagingPath,
                expectedLength,
                expectedPixelWidth,
                expectedPixelHeight,
                expectedHash: null,
                cancellationToken);
            var relativePath = GetRelativePath(hash);
            var finalPath = ResolvePath(hash, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(finalPath))
            {
                _ = VerifyPngFile(
                    finalPath,
                    expectedLength,
                    expectedPixelWidth,
                    expectedPixelHeight,
                    hash,
                    cancellationToken);
                DeleteStagingFile(stagingPath);
            }
            else
            {
                try
                {
                    File.Move(stagingPath, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    _ = VerifyPngFile(
                        finalPath,
                        expectedLength,
                        expectedPixelWidth,
                        expectedPixelHeight,
                        hash,
                        cancellationToken);
                    DeleteStagingFile(stagingPath);
                }
            }

            return new ClipboardPreparedAsset(
                hash,
                relativePath,
                "image/png",
                expectedLength,
                expectedPixelWidth,
                expectedPixelHeight);
        }
        catch
        {
            TryDeleteOwnedStagingFile(stagingPath);
            throw;
        }
    }

    public void ValidatePng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.Equals(asset.MediaType, "image/png", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The clipboard image asset has an unsupported media type.");
        }

        var path = ResolvePath(asset.Hash, asset.RelativePath);
        _ = VerifyPngFile(
            path,
            asset.ByteLength,
            asset.PixelWidth,
            asset.PixelHeight,
            asset.Hash,
            cancellationToken);
    }

    public byte[] ReadPng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.Equals(asset.MediaType, "image/png", StringComparison.Ordinal) ||
            asset.ByteLength is < 1 or > int.MaxValue)
        {
            throw new InvalidDataException(
                "The clipboard image asset cannot be read as a bounded PNG.");
        }

        var path = ResolvePath(asset.Hash, asset.RelativePath);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != asset.ByteLength)
        {
            throw new InvalidDataException(
                "The clipboard PNG asset length does not match its descriptor.");
        }

        var bytes = new byte[checked((int)asset.ByteLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    "The clipboard PNG asset ended before its declared length.");
            }

            offset += read;
        }

        using var payload = new MemoryStream(bytes, writable: false);
        _ = VerifyPngStream(
            payload,
            asset.ByteLength,
            asset.PixelWidth,
            asset.PixelHeight,
            asset.Hash,
            cancellationToken);
        return bytes;
    }

    public IClipboardPayloadLease OpenVerifiedPng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.Equals(asset.MediaType, "image/png", StringComparison.Ordinal) ||
            asset.ByteLength is < 1 or >
                ClipboardImageCaptureOptions.DefaultMaximumInputBytes)
        {
            throw new InvalidDataException(
                "The clipboard image asset cannot be opened as a bounded PNG.");
        }

        var path = ResolvePath(asset.Hash, asset.RelativePath);
        FileStream? anchor = null;
        try
        {
            anchor = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            _ = VerifyPngStream(
                anchor,
                asset.ByteLength,
                asset.PixelWidth,
                asset.PixelHeight,
                asset.Hash,
                cancellationToken);
            anchor.Position = 0;
            var lease = new ContentAssetLease(path, anchor, asset.ByteLength);
            anchor = null;
            return lease;
        }
        finally
        {
            anchor?.Dispose();
        }
    }

    public void Delete(ClipboardAssetDeleteClaim claim)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(claim);
        var path = ResolvePath(claim.Hash, claim.RelativePath);
        File.Delete(path);
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _disposed, 1);

    private void WriteStagingFile(
        Stream source,
        string stagingPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.LeaseCopyBufferBytes);
        try
        {
            using var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
            var remaining = expectedLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(remaining, buffer.Length);
                var read = source.Read(buffer, 0, requested);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "The normalized PNG ended before its declared length.");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }

            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "The normalized PNG exceeds its declared length.");
            }

            output.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string VerifyPngFile(
        string path,
        long expectedLength,
        int expectedPixelWidth,
        int expectedPixelHeight,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return VerifyPngStream(
            stream,
            expectedLength,
            expectedPixelWidth,
            expectedPixelHeight,
            expectedHash,
            cancellationToken);
    }

    private string VerifyPngStream(
        Stream stream,
        long expectedLength,
        int expectedPixelWidth,
        int expectedPixelHeight,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                "The clipboard PNG asset length does not match its descriptor.");
        }

        var hash = ComputeSha256(stream, cancellationToken);
        if (expectedHash is not null &&
            !string.Equals(hash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The clipboard PNG asset hash does not match its content-addressed path.");
        }

        stream.Position = 0;
        var payload = PngClipboardPayloadInspector.Inspect(
            stream,
            expectedLength,
            _options,
            cancellationToken);
        if (payload.PixelWidth != expectedPixelWidth ||
            payload.PixelHeight != expectedPixelHeight)
        {
            throw new InvalidDataException(
                "The clipboard PNG asset dimensions do not match its descriptor.");
        }

        stream.Position = 0;
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException(
                "The clipboard PNG asset must decode to exactly one frame.");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth != expectedPixelWidth ||
            frame.PixelHeight != expectedPixelHeight)
        {
            throw new InvalidDataException(
                "The decoded clipboard PNG dimensions do not match its descriptor.");
        }

        ForcePixelDecode(frame, cancellationToken);
        return hash;
    }

    private static string ComputeSha256(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ForcePixelDecode(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(
                source,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
        var stride = checked(converted.PixelWidth * 4);
        var rowsPerBand = Math.Max(1, (1024 * 1024) / stride);
        var buffer = ArrayPool<byte>.Shared.Rent(checked(stride * rowsPerBand));
        try
        {
            for (var row = 0; row < converted.PixelHeight; row += rowsPerBand)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = Math.Min(rowsPerBand, converted.PixelHeight - row);
                converted.CopyPixels(
                    new Int32Rect(0, row, converted.PixelWidth, rows),
                    buffer,
                    stride,
                    offset: 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string ResolvePath(string hash, string relativePath)
    {
        if (!IsLowercaseSha256(hash) ||
            !string.Equals(relativePath, GetRelativePath(hash), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The clipboard asset path is not derived from a lowercase SHA-256 value.");
        }

        var path = Path.GetFullPath(
            Path.Combine(_rootPath, hash[..2], $"{hash}.png"));
        var rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The clipboard asset path escapes its owned root.");
        }

        return path;
    }

    private static string GetRelativePath(string hash) =>
        $"{hash[..2]}/{hash}.png";

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void CleanupOwnedStagingFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _stagingPath,
                         StagingPattern,
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteOwnedStagingFile(path);
            }
        }
        catch (Exception exception)
        {
            ReportCleanupFault(new IOException(
                $"Unable to enumerate clipboard asset staging files in '{_stagingPath}'.",
                exception));
        }
    }

    private static void DeleteStagingFile(string path)
    {
        File.Delete(path);
    }

    private void TryDeleteOwnedStagingFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            ReportCleanupFault(new IOException(
                "Unable to delete a clipboard asset staging file.",
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
            // Diagnostic reporting cannot change asset ownership or cleanup.
        }
    }

    private sealed class ContentAssetLease(
        string path,
        FileStream anchor,
        long length) : IClipboardPayloadLease
    {
        private FileStream? _anchor = anchor;

        public long Length { get; } = length;

        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _anchor) is null,
                this);
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _anchor, null)?.Dispose();
    }
}

internal interface IClipboardContentAssetStore : IDisposable
{
    ClipboardPreparedAsset CommitPng(
        Stream source,
        long expectedLength,
        int expectedPixelWidth,
        int expectedPixelHeight,
        CancellationToken cancellationToken = default);

    void ValidatePng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default);

    byte[] ReadPng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default);

    IClipboardPayloadLease OpenVerifiedPng(
        ClipboardPreparedAsset asset,
        CancellationToken cancellationToken = default);

    void Delete(ClipboardAssetDeleteClaim claim);
}
