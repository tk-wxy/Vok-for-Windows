using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Vok.Core.Clipboard;

namespace Vok.Infrastructure.Windows.Clipboard;

internal sealed class WindowsClipboardWriter
{
    private static readonly UnicodeEncoding Utf16 = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IWindowsClipboardNativeApi _native;
    private readonly ClipboardKnownFormats _knownFormats;
    private readonly Guid _instanceId;
    private readonly int _copyBufferBytes;
    private ClipboardWriteReceipt? _lastReceipt;

    public WindowsClipboardWriter(
        IWindowsClipboardNativeApi native,
        ClipboardKnownFormats knownFormats,
        Guid instanceId,
        int copyBufferBytes =
            ClipboardImageCaptureOptions.DefaultLeaseCopyBufferBytes)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _knownFormats = knownFormats ?? throw new ArgumentNullException(nameof(knownFormats));
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(copyBufferBytes, 1);

        _instanceId = instanceId;
        _copyBufferBytes = copyBufferBytes;
    }

    public ClipboardWriteReceipt Write(
        nint ownerWindow,
        ClipboardReplayPayload payload,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        var formats = CreateFormats(payload, operationId);
        return WriteFormats(
            ownerWindow,
            formats,
            operationId,
            payload.SemanticHash,
            CancellationToken.None);
    }

    public ClipboardWriteReceipt WriteImage(
        nint ownerWindow,
        ClipboardImageReplayMaterialization materialization,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        var formats = CreateImageFormats(materialization, operationId);
        return WriteFormats(
            ownerWindow,
            formats,
            operationId,
            materialization.SemanticHash,
            cancellationToken);
    }

    private ClipboardWriteReceipt WriteFormats(
        nint ownerWindow,
        IReadOnlyList<WriteFormat> formats,
        Guid operationId,
        string semanticHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_native.Open(ownerWindow))
        {
            throw new InvalidOperationException(
                $"OpenClipboard failed with Win32 error {_native.GetLastError()}.");
        }

        Exception? failure = null;
        var clipboardEmptied = false;
        try
        {
            if (!_native.Empty())
            {
                throw new InvalidOperationException(
                    $"EmptyClipboard failed with Win32 error {_native.GetLastError()}.");
            }

            clipboardEmptied = true;
            _lastReceipt = null;

            foreach (var format in formats)
            {
                Transfer(format, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            if (clipboardEmptied && !_native.Empty())
            {
                failure = new AggregateException(
                    failure,
                    new InvalidOperationException(
                        "Unable to clear a partial clipboard write; " +
                        $"Win32 error {_native.GetLastError()}."));
            }
        }

        if (!_native.Close())
        {
            var closeFailure = new InvalidOperationException(
                $"CloseClipboard failed with Win32 error {_native.GetLastError()}.");
            failure = failure is null
                ? closeFailure
                : new AggregateException(failure, closeFailure);
        }

        if (failure is not null)
        {
            throw failure;
        }

        var receipt = new ClipboardWriteReceipt(
            _native.GetSequenceNumber(),
            operationId,
            semanticHash);
        _lastReceipt = receipt;
        return receipt;
    }

    public bool IsCurrent(nint ownerWindow, ClipboardWriteReceipt receipt)
    {
        if (_native.GetSequenceNumber() != receipt.RuntimeSequence ||
            _lastReceipt is not { } lastReceipt ||
            lastReceipt != receipt)
        {
            return false;
        }

        if (!_native.Open(ownerWindow))
        {
            return false;
        }

        bool matches;
        try
        {
            matches = TryReadMarker(out var marker) && marker.Matches(_instanceId, receipt);
        }
        finally
        {
            if (!_native.Close())
            {
                matches = false;
            }
        }

        return matches;
    }

    public bool IsExpectedSelfWrite(uint sequence, ReadOnlyMemory<byte> payload)
    {
        return _lastReceipt is { } receipt &&
            receipt.RuntimeSequence == sequence &&
            ClipboardInternalWriteMarker.TryDecode(payload.Span, out var marker) &&
            marker.Matches(_instanceId, receipt);
    }

    private IReadOnlyList<WriteFormat> CreateFormats(
        ClipboardReplayPayload payload,
        Guid operationId)
    {
        return payload.Kind switch
        {
            ClipboardContentKind.Text => CreateTextFormats(
                payload,
                payload.PlainText ?? throw new InvalidOperationException(
                    "A text clipboard replay payload must contain Unicode text."),
                operationId),
            ClipboardContentKind.FileList => CreateFileFormats(payload, operationId),
            _ => throw new NotSupportedException(
                $"Clipboard replay kind '{payload.Kind}' is not implemented."),
        };
    }

    private IReadOnlyList<WriteFormat> CreateTextFormats(
        ClipboardReplayPayload payload,
        string plainText,
        Guid operationId)
    {
        var formats = new List<WriteFormat>(4);
        var html = payload.Representations.FirstOrDefault(static representation =>
            representation.Kind is ClipboardRepresentationKind.Html);
        if (html is not null)
        {
            formats.Add(new WriteFormat(
                _knownFormats.Html.Id,
                AppendNull(Utf8.GetBytes(html.InlineText), terminatorBytes: 1)));
        }

        var rtf = payload.Representations.FirstOrDefault(static representation =>
            representation.Kind is ClipboardRepresentationKind.Rtf);
        if (rtf is not null)
        {
            formats.Add(new WriteFormat(
                _knownFormats.Rtf.Id,
                AppendNull(Encoding.Latin1.GetBytes(rtf.InlineText), terminatorBytes: 1)));
        }

        formats.Add(new WriteFormat(
            ClipboardKnownFormats.UnicodeText,
            AppendNull(Utf16.GetBytes(plainText), terminatorBytes: 2)));
        formats.Add(new WriteFormat(
            _knownFormats.InternalWriteMarker.Id,
            new ClipboardInternalWriteMarker(
                _instanceId,
                operationId,
                payload.SemanticHash).Encode()));
        return formats;
    }

    private IReadOnlyList<WriteFormat> CreateImageFormats(
        ClipboardImageReplayMaterialization materialization,
        Guid operationId) =>
    [
        new WriteFormat(
            _knownFormats.Png.Id,
            materialization.CanonicalPngByteLength,
            materialization.OpenCanonicalPngReadStream),
        new WriteFormat(
            ClipboardKnownFormats.DibV5,
            materialization.DibV5ByteLength,
            materialization.OpenDibV5ReadStream),
        new WriteFormat(
            _knownFormats.InternalWriteMarker.Id,
            new ClipboardInternalWriteMarker(
                _instanceId,
                operationId,
                materialization.SemanticHash).Encode()),
    ];

    private IReadOnlyList<WriteFormat> CreateFileFormats(
        ClipboardReplayPayload payload,
        Guid operationId)
    {
        if (payload.FileIntent is not (
                ClipboardFileTransferIntent.Copy or
                ClipboardFileTransferIntent.Move) ||
            payload.FileReferences.Count == 0)
        {
            throw new InvalidOperationException(
                "A file replay payload requires an explicit COPY or MOVE intent and at least one path.");
        }

        var actualHash = ClipboardFileSemanticIdentity.Compute(
            payload.FileIntent.Value,
            payload.FileReferences);
        if (!string.Equals(actualHash, payload.SemanticHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The file replay semantic hash does not match its paths and intent.");
        }

        var effect = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            effect,
            payload.FileIntent is ClipboardFileTransferIntent.Copy ? 1u : 2u);
        return
        [
            new WriteFormat(
                ClipboardKnownFormats.FileDrop,
                ClipboardFileDropCodec.Encode(payload.FileReferences)),
            new WriteFormat(_knownFormats.PreferredDropEffect.Id, effect),
            new WriteFormat(
                _knownFormats.InternalWriteMarker.Id,
                new ClipboardInternalWriteMarker(
                    _instanceId,
                    operationId,
                    payload.SemanticHash).Encode()),
        ];
    }

    private void Transfer(
        WriteFormat format,
        CancellationToken cancellationToken)
    {
        if (format.Length is < 1 ||
            (ulong)format.Length > (ulong)nuint.MaxValue)
        {
            throw new InvalidDataException(
                "The clipboard write format has an invalid byte length.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var handle = _native.AllocateGlobal(checked((nuint)format.Length));
        if (handle == 0)
        {
            throw new InvalidOperationException(
                $"GlobalAlloc failed with Win32 error {_native.GetLastError()}.");
        }

        var transferred = false;
        try
        {
            var pointer = _native.LockGlobal(handle);
            if (pointer == 0)
            {
                throw new InvalidOperationException(
                    $"GlobalLock failed with Win32 error {_native.GetLastError()}.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(_copyBufferBytes);
            try
            {
                using var stream = format.OpenRead();
                var offset = 0L;
                while (offset < format.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(
                        format.Length - offset,
                        buffer.Length);
                    var read = stream.Read(buffer, 0, requested);
                    if (read == 0)
                    {
                        throw new InvalidDataException(
                            "The clipboard write payload ended before its declared length.");
                    }

                    Marshal.Copy(
                        buffer,
                        0,
                        pointer + checked((nint)offset),
                        read);
                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        "The clipboard write payload exceeds its declared length.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _native.UnlockGlobal(handle);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_native.SetData(format.Id, handle) == 0)
            {
                throw new InvalidOperationException(
                    $"SetClipboardData failed for format {format.Id} with Win32 error {_native.GetLastError()}.");
            }

            transferred = true;
        }
        finally
        {
            if (!transferred)
            {
                _ = _native.FreeGlobal(handle);
            }
        }
    }

    private bool TryReadMarker(out ClipboardInternalWriteMarker marker)
    {
        marker = default;
        var handle = _native.GetData(_knownFormats.InternalWriteMarker.Id);
        if (handle == 0)
        {
            return false;
        }

        var size = _native.GetGlobalSize(handle);
        if (size == 0 || size > 4096)
        {
            return false;
        }

        var pointer = _native.LockGlobal(handle);
        if (pointer == 0)
        {
            return false;
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return ClipboardInternalWriteMarker.TryDecode(bytes, out marker);
        }
        finally
        {
            _native.UnlockGlobal(handle);
        }
    }

    private static byte[] AppendNull(byte[] value, int terminatorBytes)
    {
        var result = new byte[checked(value.Length + terminatorBytes)];
        value.CopyTo(result, 0);
        return result;
    }

    private sealed class WriteFormat
    {
        private readonly Func<Stream> _openRead;

        public WriteFormat(uint id, byte[] bytes)
            : this(
                id,
                bytes?.LongLength ?? throw new ArgumentNullException(nameof(bytes)),
                () => new MemoryStream(bytes, writable: false))
        {
        }

        public WriteFormat(uint id, long length, Func<Stream> openRead)
        {
            Id = id;
            Length = length;
            _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        }

        public uint Id { get; }

        public long Length { get; }

        public Stream OpenRead()
        {
            var stream = _openRead();
            if (stream is null || !stream.CanRead)
            {
                stream?.Dispose();
                throw new InvalidDataException(
                    "The clipboard write payload did not provide a readable stream.");
            }

            return stream;
        }
    }
}
