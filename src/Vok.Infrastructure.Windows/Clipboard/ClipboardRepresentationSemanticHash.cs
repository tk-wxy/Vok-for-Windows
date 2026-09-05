using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class ClipboardRepresentationSemanticHash
{
    public static string Compute(
        IReadOnlyList<ClipboardStoredRepresentation> representations)
    {
        ArgumentNullException.ThrowIfNull(representations);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var representation in representations.OrderBy(static value => value.Kind))
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, (int)representation.Kind);
            hash.AppendData(length);
            var bytes = Encoding.UTF8.GetBytes(representation.InlineText);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
