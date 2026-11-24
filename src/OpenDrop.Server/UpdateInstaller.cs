using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

internal static class UpdateInstaller
{
    public static string Install(UpdatePackage package, string? installRoot = null)
    {
        installRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenDropWindows", "Versions");
        installRoot = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(installRoot);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(installRoot, $".{id}.part");
        var destination = Path.Combine(installRoot, $"OpenDrop-{package.Version.ToString(3)}-{id[..8]}");
        using var stream = File.OpenRead(package.Path);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(stream));
        if (!digest.Equals(package.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Prüfsumme des Updatepakets hat sich geändert.");

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1500 || zip.Entries.Sum(entry => entry.Length) > 600L * 1024 * 1024)
            throw new InvalidDataException("Das Installationspaket ist zu groß.");

        try
        {
            zip.ExtractToDirectory(staging);
            foreach (var name in new[] { "OpenDrop.Windows.exe", "OpenDrop.Windows.dll", "QRCoder.dll",
                         "WebView2Loader.dll", "owner.html", "wwwroot/index.html" })
                if (!File.Exists(Path.Combine(staging, name)))
                    throw new InvalidDataException($"Im Updatepaket fehlt {name}.");

            var executable = Path.Combine(staging, "OpenDrop.Windows.exe");
            var version = FileVersionInfo.GetVersionInfo(executable);
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart) != package.Version)
                throw new InvalidDataException("Die Versionsnummer der EXE stimmt nicht mit dem Release überein.");

            Directory.Move(staging, destination);
            return Path.Combine(destination, "OpenDrop.Windows.exe");
        }
        finally
        {
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }
}
