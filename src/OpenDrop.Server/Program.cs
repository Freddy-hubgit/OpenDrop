using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using QRCoder;

const long maxUploadBytes = 100L * 1024 * 1024;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var settings = builder.Configuration.GetSection("OpenDrop");
var port = settings.GetValue("Port", 5180);
if (port is < 1024 or > 65535)
    throw new InvalidOperationException("OpenDrop:Port muss zwischen 1024 und 65535 liegen.");

var localUrl = $"http://127.0.0.1:{port}";
var transferUrl = localUrl;
IPAddress? lanAddress = null;
X509Certificate2? certificate = null;
X509Certificate2Collection? certificateChain = null;

if (settings["LanIp"] is { Length: > 0 } lanIp)
{
    if (!IPAddress.TryParse(lanIp, out lanAddress) ||
        lanAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        throw new InvalidOperationException("OpenDrop:LanIp muss eine private IPv4-Adresse sein.");

    var bytes = lanAddress.GetAddressBytes();
    if (!(bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
          (bytes[0] == 192 && bytes[1] == 168)))
        throw new InvalidOperationException("Als LAN-Adresse sind private IPv4-Adressen erlaubt.");

    var host = settings["LanHost"] ?? lanAddress.ToString();
    if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        throw new InvalidOperationException("OpenDrop:LanHost muss ein DNS-Name oder eine IPv4-Adresse sein.");

    var pfxPath = settings["PfxPath"];
    if (string.IsNullOrWhiteSpace(pfxPath))
        throw new InvalidOperationException("LAN-Empfang erfordert OpenDrop:PfxPath und ein vertrauenswürdiges TLS-Zertifikat.");

    certificateChain = X509CertificateLoader.LoadPkcs12CollectionFromFile(
        Path.GetFullPath(pfxPath), settings["PfxPassword"]);
    certificate = certificateChain.OfType<X509Certificate2>().SingleOrDefault(item => item.HasPrivateKey)
        ?? throw new InvalidOperationException("Das PFX benötigt ein Serverzertifikat mit privatem Schlüssel.");
    certificateChain.Remove(certificate);
    transferUrl = new UriBuilder("https", host, port).Uri.GetLeftPart(UriPartial.Authority);
}

using var tlsCertificate = certificate;
var allowedLanHost = new Uri(transferUrl).Host;
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = maxUploadBytes;
    server.Listen(IPAddress.Loopback, port, listener => listener.Protocols = HttpProtocols.Http1);
    if (lanAddress is not null)
        server.Listen(lanAddress, port, listener => listener.UseHttps(tlsCertificate!,
            https => https.ServerCertificateChain = certificateChain));
});

var inbox = Path.GetFullPath(settings["Inbox"] ?? WindowsDownloads.GetPath());
var store = new Inbox(inbox);
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var tokenBytes = Encoding.ASCII.GetBytes(token);
var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
var invitation = $"{transferUrl}/#pair={token}";
var qr = PngByteQRCodeHelper.GetQRCode(invitation, QRCodeGenerator.ECCLevel.Q, 6);
using var uploadSlot = new SemaphoreSlim(1);
await using var app = builder.Build();

app.Use(async (context, next) =>
{
    var expectedHost = context.Request.IsHttps ? allowedLanHost : "127.0.0.1";
    if (!string.Equals(context.Request.Host.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
        context.Request.Host.Port != port)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/owner", (HttpContext context) =>
    IsOwner(context) ? Results.File(Path.Combine(app.Environment.ContentRootPath, "owner.html"),
        "text/html; charset=utf-8") : Results.NotFound());
app.MapGet("/owner/invitation", (HttpContext context) => IsOwner(context)
    ? Results.Ok(new { invitation, expiresAt, inbox, receipts = store.RecentReceipts() })
    : Results.NotFound());
app.MapGet("/owner/airdrop", (HttpContext context) => IsOwner(context)
    ? Results.Ok(AirDropStatus.Read())
    : Results.NotFound());
app.MapGet("/owner/qr.png", (HttpContext context) => IsOwner(context)
    ? Results.File(qr, "image/png")
    : Results.NotFound());

app.MapPost("/api/upload", async (HttpContext context) =>
{
    var request = context.Request;
    var authorization = request.Headers.Authorization.ToString();
    if (DateTimeOffset.UtcNow >= expiresAt ||
        authorization.Length != 7 + tokenBytes.Length ||
        !authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
        !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(authorization[7..]), tokenBytes))
        return Results.Problem("Die Einladung fehlt oder ist abgelaufen.", statusCode: 401);

    if (!string.Equals(request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        return Results.Problem("Dateien müssen als Binärdaten übertragen werden.", statusCode: 415);

    if (request.ContentLength > maxUploadBytes)
        return Results.Problem("Maximal 100 MiB pro Datei.", statusCode: 413);

    var names = request.Query["name"];
    if (names.Count != 1 || !Inbox.IsValidName(names[0]))
        return Results.Problem("Ungültiger Dateiname.", statusCode: 400);

    if (!await uploadSlot.WaitAsync(0, context.RequestAborted))
        return Results.Problem("Es läuft bereits eine Übertragung. Bitte versuche es gleich erneut.", statusCode: 429);

    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromMinutes(5));
    try
    {
        var receipt = await store.SaveAsync(names[0]!, request.Body, maxUploadBytes, deadline.Token);
        app.Logger.LogInformation("Empfangen {Id}, {Bytes} Byte", receipt.Id, receipt.Bytes);
        return Results.Json(receipt, statusCode: 201);
    }
    catch (UploadTooLargeException)
    {
        return Results.Problem("Maximal 100 MiB pro Datei.", statusCode: 413);
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException error)
    {
        return Results.Problem("Der Upload konnte nicht vollständig gelesen werden.", statusCode: error.StatusCode);
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        return Results.Problem("Die Übertragung wurde abgebrochen oder hat zu lange gedauert.", statusCode: 408);
    }
    catch (IOException error)
    {
        app.Logger.LogError(error, "Dateiübertragung fehlgeschlagen");
        return Results.Problem("Die Datei konnte nicht gespeichert werden.", statusCode: 500);
    }
    catch (UnauthorizedAccessException error)
    {
        app.Logger.LogError(error, "Kein Schreibzugriff auf den Empfangsordner");
        return Results.Problem("Der Empfangsordner ist nicht beschreibbar.", statusCode: 500);
    }
    finally
    {
        uploadSlot.Release();
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"OpenDrop Windows: {localUrl}/owner");
    Console.WriteLine($"Empfangsordner: {inbox}");
});
try
{
#if OPENDROP_DESKTOP
    try
    {
        await app.StartAsync();
    }
    catch (Exception error)
    {
        await DesktopWindow.ShowAsync(localUrl + "/owner", "Der Empfangsserver konnte nicht starten: " + error.Message);
        return;
    }

    await DesktopWindow.ShowAsync(localUrl + "/owner");
    await app.StopAsync();
#else
    await app.RunAsync();
#endif
}
finally
{
    if (certificateChain is not null)
        foreach (var intermediate in certificateChain)
            intermediate.Dispose();
}

static bool IsOwner(HttpContext context) =>
    !context.Request.IsHttps &&
    context.Connection.RemoteIpAddress is { } remote &&
    IPAddress.IsLoopback(remote);
