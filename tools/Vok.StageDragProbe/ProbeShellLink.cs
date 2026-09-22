using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Vok.StageDragProbe;

internal static class ProbeShellLink
{
    private static readonly Guid ShellLinkClassId =
        new("00021401-0000-0000-C000-000000000046");

    internal static void CreateAndVerify(
        string linkPath,
        string targetPath,
        string description)
    {
        EnsureSta();
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            throw new IOException($"The Shell link path already exists: {linkPath}");
        }

        var temporaryPath = linkPath + ".creating-" + Guid.NewGuid().ToString("N") + ".lnk";
        var instance = CreateInstance();
        try
        {
            var shellLink = (IProbeShellLinkW)instance;
            Marshal.ThrowExceptionForHR(shellLink.SetPath(targetPath));
            Marshal.ThrowExceptionForHR(shellLink.SetDescription(description));
            ((IPersistFile)instance).Save(temporaryPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }

        if (!File.Exists(temporaryPath))
        {
            throw new IOException("The Shell link was not created.");
        }

        var observedTarget = ReadTarget(temporaryPath);
        if (!string.Equals(
                StageDragProbeOptions.NormalizePath(observedTarget),
                StageDragProbeOptions.NormalizePath(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The persisted Shell link did not resolve to its fixture target.");
        }

        File.Move(temporaryPath, linkPath, overwrite: false);
        if (!File.Exists(linkPath))
        {
            throw new IOException("The verified Shell link was not published.");
        }
    }

    internal static string ReadTarget(string linkPath)
    {
        EnsureSta();
        if (!File.Exists(linkPath))
        {
            throw new FileNotFoundException("The Shell link does not exist.", linkPath);
        }

        var instance = CreateInstance();
        try
        {
            ((IPersistFile)instance).Load(linkPath, 0);
            var buffer = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(
                ((IProbeShellLinkW)instance).GetPath(
                    buffer,
                    buffer.Capacity,
                    0,
                    0x00000004));
            if (buffer.Length == 0)
            {
                throw new InvalidDataException("The Shell link target was empty.");
            }

            return Path.GetFullPath(buffer.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static object CreateInstance()
    {
        var type = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true) ??
            throw new InvalidOperationException("The Shell Link COM class is unavailable.");
        return Activator.CreateInstance(type) ??
            throw new InvalidOperationException("The Shell Link COM class could not be created.");
    }

    private static void EnsureSta()
    {
        if (Thread.CurrentThread.GetApartmentState() is not ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "Shell Link persistence must run on the probe STA thread.");
        }
    }
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProbeShellLinkW
{
    [PreserveSig]
    int GetPath(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
        int characterCount,
        nint findData,
        uint flags);

    [PreserveSig]
    int GetIdList(out nint itemIdList);

    [PreserveSig]
    int SetIdList(nint itemIdList);

    [PreserveSig]
    int GetDescription(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description,
        int characterCount);

    [PreserveSig]
    int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

    [PreserveSig]
    int GetWorkingDirectory(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
        int characterCount);

    [PreserveSig]
    int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

    [PreserveSig]
    int GetArguments(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
        int characterCount);

    [PreserveSig]
    int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

    [PreserveSig]
    int GetHotkey(out short hotkey);

    [PreserveSig]
    int SetHotkey(short hotkey);

    [PreserveSig]
    int GetShowCommand(out int showCommand);

    [PreserveSig]
    int SetShowCommand(int showCommand);

    [PreserveSig]
    int GetIconLocation(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
        int characterCount,
        out int iconIndex);

    [PreserveSig]
    int SetIconLocation(
        [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
        int iconIndex);

    [PreserveSig]
    int SetRelativePath(
        [MarshalAs(UnmanagedType.LPWStr)] string relativePath,
        uint reserved);

    [PreserveSig]
    int Resolve(nint ownerWindow, uint flags);

    [PreserveSig]
    int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}
