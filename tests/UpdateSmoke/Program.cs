using System.IO.Compression;
using System.Runtime.InteropServices;

if (args is ["current"])
{
    var status = await ReleaseUpdater.DownloadAsync(CancellationToken.None);
    Require(status == "OpenDrop ist aktuell.", status);
    Console.WriteLine("Die aktuelle Version lädt kein Update herunter.");
    return;
}

if (args is not [var version] || !Version.TryParse(version, out var latest))
    throw new ArgumentException("Die Nummer des aktuellen GitHub-Releases fehlt.");

var normalized = new Version(latest.Major, latest.Minor, latest.Build, 0);
var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "OpenDropWindows", "Updates");
var package = Path.Combine(folder, $"OpenDrop-{normalized}-win-{architecture}.zip");
Require(!File.Exists(package), "Der Download-Test benötigt einen frischen Runner ohne vorhandenes Update.");

var result = await ReleaseUpdater.CheckAsync(CancellationToken.None);
var downloaded = result.Message;
Require(downloaded.StartsWith("Update ", StringComparison.Ordinal) &&
    downloaded.Contains(" heruntergeladen: ", StringComparison.Ordinal) && File.Exists(package), downloaded);
using (var zip = ZipFile.OpenRead(package))
{
    foreach (var filename in new[] { "OpenDrop.Windows.exe", "QRCoder.dll", "WebView2Loader.dll", "owner.html" })
        Require(zip.GetEntry(filename) is { Length: > 0 }, $"Eintrag im Update-ZIP fehlt: {filename}");
}

var installed = UpdateInstaller.Install(result.Package ?? throw new InvalidOperationException("Updatepaket fehlt."),
    Path.Combine(Path.GetTempPath(), $"OpenDrop-UpdateSmoke-{Guid.NewGuid():N}"));
Require(File.Exists(installed), "Die installierte EXE fehlt.");
Console.WriteLine($"Version {latest} wurde aus dem geprüften ZIP installiert.");

var cached = await ReleaseUpdater.DownloadAsync(CancellationToken.None);
Require(cached.StartsWith("Update ", StringComparison.Ordinal) &&
    cached.Contains(" bereit: ", StringComparison.Ordinal), cached);
Require(!Directory.EnumerateFiles(folder, "*.part").Any(), "Der Download hat eine temporäre Datei hinterlassen.");
Console.WriteLine($"Release {latest} erkannt, heruntergeladen, geprüft und aus dem Cache wiederverwendet.");

static void Require(bool condition, string error)
{
    if (!condition) throw new InvalidOperationException(error);
}
