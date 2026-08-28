using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Vok.Core.Clipboard;

public static class ClipboardFileSemanticIdentity
{
    private static readonly byte[] VersionPrefix =
        "vok:file-list:v1\0"u8.ToArray();

    public static string Compute(
        ClipboardFileTransferIntent intent,
        IReadOnlyList<ClipboardFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException(
                "A file-list identity requires at least one file.",
                nameof(files));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(VersionPrefix);
        Span<byte> number = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(number, (int)intent);
        hash.AppendData(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, files.Count);
        hash.AppendData(number);

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index] ?? throw new ArgumentException(
                "A file-list identity cannot contain null entries.",
                nameof(files));
            if (file.Ordinal != index || string.IsNullOrWhiteSpace(file.CanonicalPath))
            {
                throw new ArgumentException(
                    "File ordinals must be contiguous and canonical paths must be present.",
                    nameof(files));
            }

            var normalized = file.CanonicalPath.ToUpperInvariant();
            var bytes = Encoding.UTF8.GetBytes(normalized);
            BinaryPrimitives.WriteInt32LittleEndian(number, bytes.Length);
            hash.AppendData(number);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
