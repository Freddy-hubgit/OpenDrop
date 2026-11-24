using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ReleaseUpdater
{
    private const string Repository = "https://api.github.com/repos/Freddy-hubgit/OpenDrop";
    private const long MaximumSize = 300L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });

    public static async Task<string> DownloadAsync(CancellationToken cancellation) =>
        (await CheckAsync(cancellation)).Message;

    public static async Task<UpdateResult> CheckAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var credential = Environment.GetEnvironmentVariable("OPENDROP_GITHUB_TOKEN");
        string? temp = null;
        try
        {
            using var releaseRequest = Request(Repository + "/releases/latest", "application/vnd.github+json", credential);
            using var release = await Client.SendAsync(releaseRequest, token);
            if (release.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                return new("Kein Release erreichbar. Das private Repository benötigt GitHub-Lesezugriff und einen veröffentlichten Release.");
            release.EnsureSuccessStatusCode();
            using var metadata = JsonDocument.Parse(await release.Content.ReadAsStreamAsync(token));
            var tag = metadata.RootElement.GetProperty("tag_name").GetString();
            if (tag is null || !Version.TryParse(tag.TrimStart('v'), out var remote))
                return new("Der GitHub-Release hat keine numerische Versionsnummer.");
            var current = Assembly.GetExecutingAssembly().GetName().Version!;
            var normalized = new Version(remote.Major, remote.Minor, Math.Max(remote.Build, 0), Math.Max(remote.Revision, 0));
            if (normalized <= current) return new("OpenDrop ist aktuell.");

            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var name = $"OpenDrop-Windows-win-{architecture}.zip";
            foreach (var asset in metadata.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                var size = asset.GetProperty("size").GetInt64();
                var digest = asset.GetProperty("digest").GetString();
                if (size is <= 0 or > MaximumSize || digest is null ||
                    !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
                    return new("Der Release enthält keine prüfbaren Paketdaten.");

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDropWindows", "Updates");
                Directory.CreateDirectory(folder);
                var destination = Path.Combine(folder, $"OpenDrop-{normalized}-win-{architecture}.zip");
                if (File.Exists(destination))
                {
                    await using var existing = File.OpenRead(destination);
                    if (existing.Length == size && await MatchesAsync(existing, digest, token))
                        return new($"Update {remote} bereit: {destination}", new(destination, normalized, digest));
                }

                var id = asset.GetProperty("id").GetInt64();
                using var downloadRequest = Request($"{Repository}/releases/assets/{id}", "application/octet-stream", credential);
                using var initial = await Client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, token);
                var location = initial.Headers.Location;
                using var redirected = initial.StatusCode == HttpStatusCode.Found && location is { IsAbsoluteUri: true }
                    && location.Scheme == "https" && location.Host == "release-assets.githubusercontent.com"
                    ? await Client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, token) : null;
                var download = redirected ?? initial;
                download.EnsureSuccessStatusCode();

                temp = Path.Combine(folder, $"{Guid.NewGuid():N}.part");
                await using (var input = await download.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        if (output.Length + read > size) return new("Das Updatepaket ist größer als angekündigt.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    output.Position = 0;
                    if (output.Length != size || !await MatchesAsync(output, digest, token))
                        return new("Die SHA-256-Prüfung des Updates ist fehlgeschlagen.");
                }
                File.Move(temp, destination, overwrite: true);
                return new($"Update {remote} heruntergeladen: {destination}", new(destination, normalized, digest));
            }
            return new("Der neuere Release enthält kein Paket für diesen Windows-Prozessor.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new("Die Updateprüfung ist fehlgeschlagen. Der Empfangsserver läuft weiter.");
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static HttpRequestMessage Request(string url, string accept, string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("OpenDropWindows/1.0");
        request.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(credential))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Trim());
        return request;
    }

    private static async Task<bool> MatchesAsync(Stream stream, string digest, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, token))
            .Equals(digest[7..], StringComparison.OrdinalIgnoreCase);
}

internal sealed record UpdateResult(string Message, UpdatePackage? Package = null);
internal sealed record UpdatePackage(string Path, Version Version, string Digest);
