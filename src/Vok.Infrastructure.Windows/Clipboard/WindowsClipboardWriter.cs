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
    private ClipboardWriteReceipt? _lastReceipt;

    public WindowsClipboardWriter(
        IWindowsClipboardNativeApi native,
        ClipboardKnownFormats knownFormats,
        Guid instanceId)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _knownFormats = knownFormats ?? throw new ArgumentNullException(nameof(knownFormats));
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceId));
        }

        _instanceId = instanceId;
    }

    public ClipboardWriteReceipt Write(
        nint ownerWindow,
        ClipboardReplayPayload payload,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        if (payload.Kind is not ClipboardContentKind.Text)
        {
            throw new NotSupportedException(
                $"Clipboard replay kind '{payload.Kind}' is not implemented in M2.1.");
        }

        var plainText = payload.PlainText ?? throw new InvalidOperationException(
            "A text clipboard replay payload must contain Unicode text.");
        var formats = CreateFormats(payload, plainText, operationId);
        if (!_native.Open(ownerWindow))
        {
            throw new InvalidOperationException(
                $"OpenClipboard failed with Win32 error {_native.GetLastError()}.");
        }

        Exception? failure = null;
        try
        {
            if (!_native.Empty())
            {
                throw new InvalidOperationException(
                    $"EmptyClipboard failed with Win32 error {_native.GetLastError()}.");
            }

            foreach (var format in formats)
            {
                Transfer(format.Id, format.Bytes);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
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
            payload.SemanticHash);
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

    private void Transfer(uint format, byte[] bytes)
    {
        var handle = _native.AllocateGlobal((nuint)bytes.Length);
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

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                _native.UnlockGlobal(handle);
            }

            if (_native.SetData(format, handle) == 0)
            {
                throw new InvalidOperationException(
                    $"SetClipboardData failed for format {format} with Win32 error {_native.GetLastError()}.");
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

    private sealed record WriteFormat(uint Id, byte[] Bytes);
}
