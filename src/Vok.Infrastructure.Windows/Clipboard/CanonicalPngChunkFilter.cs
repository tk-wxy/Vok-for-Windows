using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Vok.Infrastructure.Windows.Clipboard;

internal static class CanonicalPngChunkFilter
{
    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static void CopyPixelChunks(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead || !input.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException(
                "PNG filtering requires a seekable input and writable output.");
        }

        input.Position = 0;
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature))
        {
            throw new InvalidDataException("The encoded canonical PNG signature is invalid.");
        }

        output.Write(signature);
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            Span<byte> header = stackalloc byte[8];
            while (!sawEnd)
            {
                input.ReadExactly(header);
                var dataLength = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (dataLength > input.Length - input.Position - 4)
                {
                    throw new InvalidDataException(
                        "The encoded canonical PNG contains a truncated chunk.");
                }

                var type = header[4..8];
                var keep = ShouldKeep(type);
                if ((type[0] & 0x20) == 0 && !keep)
                {
                    throw new InvalidDataException(
                        "The encoded canonical PNG contains an unexpected critical chunk.");
                }

                if (type.SequenceEqual("IHDR"u8))
                {
                    if (sawHeader || dataLength != 13)
                    {
                        throw new InvalidDataException(
                            "The encoded canonical PNG IHDR is invalid.");
                    }

                    sawHeader = true;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    sawImageData = true;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    if (dataLength != 0 || !sawImageData)
                    {
                        throw new InvalidDataException(
                            "The encoded canonical PNG IEND is invalid.");
                    }

                    sawEnd = true;
                }

                if (keep)
                {
                    output.Write(header);
                }

                var remaining = checked((long)dataLength + 4);
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    input.ReadExactly(buffer.AsSpan(0, count));
                    if (keep)
                    {
                        output.Write(buffer, 0, count);
                    }

                    remaining -= count;
                }
            }

            if (!sawHeader || input.Position != input.Length)
            {
                throw new InvalidDataException(
                    "The encoded canonical PNG is incomplete or has trailing bytes.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ShouldKeep(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("IHDR"u8) ||
        type.SequenceEqual("PLTE"u8) ||
        type.SequenceEqual("tRNS"u8) ||
        type.SequenceEqual("IDAT"u8) ||
        type.SequenceEqual("IEND"u8);
}
