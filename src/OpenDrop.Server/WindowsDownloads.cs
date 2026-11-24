using System.Runtime.InteropServices;

internal static class WindowsDownloads
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private const uint DontVerify = 0x00004000;
    private const int ChangedApartment = unchecked((int)0x80010106);

    public static string GetPath()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Gib außerhalb von Windows einen OpenDrop:Inbox-Pfad an.");

        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized != ChangedApartment)
            Marshal.ThrowExceptionForHR(initialized);

        IntPtr path = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(in DownloadsId, DontVerify, IntPtr.Zero, out path));
            return Marshal.PtrToStringUni(path)
                ?? throw new InvalidOperationException("Der Windows-Downloadordner konnte nicht ermittelt werden.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
            if (initialized >= 0)
                CoUninitialize();
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
