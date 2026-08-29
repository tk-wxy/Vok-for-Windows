using System.IO;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class SeekablePrefixStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _position;

    public SeekablePrefixStream(
        ReadOnlyMemory<byte> prefix,
        Stream inner,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException(
                "The inner stream must be readable and seekable.",
                nameof(inner));
        }

        _prefix = prefix;
        _inner = inner;
        _leaveOpen = leaveOpen;
        _inner.Position = 0;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => checked(_prefix.Length + _inner.Length);

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _position >= Length)
        {
            return 0;
        }

        var copied = 0;
        if (_position < _prefix.Length)
        {
            var prefixOffset = checked((int)_position);
            var prefixCount = Math.Min(
                buffer.Length,
                _prefix.Length - prefixOffset);
            _prefix.Span.Slice(prefixOffset, prefixCount).CopyTo(buffer);
            _position += prefixCount;
            copied += prefixCount;
            buffer = buffer[prefixCount..];
        }

        if (!buffer.IsEmpty && _position < Length)
        {
            _inner.Position = _position - _prefix.Length;
            var read = _inner.Read(buffer);
            _position += read;
            copied += read;
        }

        return copied;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Cannot seek before the start of the stream.");
        }

        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
