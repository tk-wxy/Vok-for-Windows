namespace Vok.Infrastructure.Windows.Clipboard;

internal interface IWindowsClipboardNativeApi
{
    uint GetSequenceNumber();

    uint RegisterFormat(string name);

    bool Open(nint ownerWindow);

    bool Close();

    bool Empty() => throw new NotSupportedException();

    uint EnumerateFormats(uint previousFormat);

    string? GetFormatName(uint format);

    nint GetData(uint format);

    nint SetData(uint format, nint handle) => throw new NotSupportedException();

    nint AllocateGlobal(nuint bytes) => throw new NotSupportedException();

    nint FreeGlobal(nint handle) => throw new NotSupportedException();

    nuint GetGlobalSize(nint handle);

    nint LockGlobal(nint handle);

    void UnlockGlobal(nint handle);

    void ResetLastError();

    int GetLastError();
}
