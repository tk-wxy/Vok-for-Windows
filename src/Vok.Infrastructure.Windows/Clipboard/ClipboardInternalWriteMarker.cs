using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal readonly record struct ClipboardInternalWriteMarker(
    Guid InstanceId,
    Guid OperationId,
    string SemanticHash)
{
    private const int MagicLength = 4;
    private const int GuidLength = 16;
    private const int HashLength = 32;
    private const int PayloadLength = MagicLength + GuidLength + GuidLength + HashLength;
    private static ReadOnlySpan<byte> Magic => "VOK1"u8;

    public byte[] Encode()
    {
        if (InstanceId == Guid.Empty || OperationId == Guid.Empty)
        {
            throw new InvalidOperationException("Clipboard write marker identifiers cannot be empty.");
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString(SemanticHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The clipboard semantic hash is not valid hexadecimal.",
                exception);
        }

        if (hash.Length != HashLength)
        {
            throw new InvalidOperationException("The clipboard semantic hash must contain 32 bytes.");
        }

        var payload = new byte[PayloadLength];
        Magic.CopyTo(payload);
        InstanceId.TryWriteBytes(payload.AsSpan(MagicLength, GuidLength));
        OperationId.TryWriteBytes(payload.AsSpan(MagicLength + GuidLength, GuidLength));
        hash.CopyTo(payload, MagicLength + (GuidLength * 2));
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ClipboardInternalWriteMarker marker)
    {
        if (payload.Length != PayloadLength || !payload[..MagicLength].SequenceEqual(Magic))
        {
            marker = default;
            return false;
        }

        var instanceId = new Guid(payload.Slice(MagicLength, GuidLength));
        var operationId = new Guid(payload.Slice(MagicLength + GuidLength, GuidLength));
        var semanticHash = Convert.ToHexString(
            payload.Slice(MagicLength + (GuidLength * 2), HashLength)).ToLowerInvariant();
        marker = new ClipboardInternalWriteMarker(instanceId, operationId, semanticHash);
        return true;
    }

    public bool Matches(Guid instanceId, ClipboardWriteReceipt receipt) =>
        InstanceId == instanceId &&
        OperationId == receipt.OperationId &&
        string.Equals(SemanticHash, receipt.SemanticHash, StringComparison.Ordinal);
}
