using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

internal sealed record IncomingOffer(string Name, string Secret);
internal sealed record ConnectionChoice(string Id, bool Accept);
internal sealed record TargetDevice(string Address);
internal sealed record OfferResult(string State, string? Id = null, string? Code = null, string? Invitation = null);
internal sealed record OutgoingOffer(string Address, string Id);
internal sealed record DeviceEndpoint(string Origin, string Pin);

internal sealed class ConnectionRequests(string? pin, string transferOrigin)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Offer> inbox = new();
    private readonly Dictionary<IPAddress, Rejections> rejections = new();
    private readonly Dictionary<string, (IPAddress Address, DateTimeOffset Expiry)> grants = new();
    private sealed record Rejections(int Count, DateTimeOffset Until);
    private sealed record Offer(IPAddress Address, string Name, string Secret, string Code,
        DateTimeOffset Expiry, string State = "pending", string? Grant = null);

    public object[] Pending()
    {
        lock (gate)
        {
            ClearExpired();
            return inbox.Where(item => item.Value.State == "pending")
                .Select(item => (object)new { id = item.Key, item.Value.Name, item.Value.Code }).ToArray();
        }
    }

    public OfferResult Receive(IPAddress address, IncomingOffer request)
    {
        if (pin is null || request.Name is not { Length: > 0 and <= 64 } ||
            request.Name.Any(char.IsControl) || !IsSecret(request.Secret))
            return new("invalid");

        lock (gate)
        {
            ClearExpired();
            if (rejections.TryGetValue(address, out var blocked) && blocked.Count >= 3)
                return new("cooldown");
            if (inbox.Values.Any(offer => offer.Address.Equals(address) && offer.State == "pending") ||
                inbox.Count >= 128 || grants.Count >= 128 || rejections.Count >= 128)
                return new("busy");
            var id = Guid.NewGuid().ToString("N");
            var code = Code(pin, request.Secret);
            inbox.Add(id, new(address, request.Name, request.Secret, code, DateTimeOffset.UtcNow.AddSeconds(90)));
            return new("pending", id, code);
        }
    }

    public OfferResult Respond(ConnectionChoice choice)
    {
        lock (gate)
        {
            ClearExpired();
            if (!inbox.TryGetValue(choice.Id, out var offer) || offer.State != "pending") return new("expired");
            if (choice.Accept)
            {
                var grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                grants.Add(grant, (offer.Address, DateTimeOffset.UtcNow.AddMinutes(5)));
                inbox[choice.Id] = offer with { State = "accepted", Grant = grant };
                rejections.Remove(offer.Address);
                return new("accepted");
            }

            var count = rejections.TryGetValue(offer.Address, out var previous) ? previous.Count + 1 : 1;
            rejections[offer.Address] = new(count, DateTimeOffset.UtcNow.AddMinutes(count >= 3 ? 5 : 30));
            var state = count >= 3 ? "cooldown" : "declined";
            inbox[choice.Id] = offer with { State = state };
            return new(state);
        }
    }

    public OfferResult Status(IPAddress address, string id, string secret)
    {
        lock (gate)
        {
            ClearExpired();
            if (!inbox.TryGetValue(id, out var offer) || !offer.Address.Equals(address) || !IsSecret(secret) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(offer.Secret), Convert.FromHexString(secret)))
                return new("expired");
            return new(offer.State, Invitation: offer.Grant is not null
                ? $"{transferOrigin}/#pair={offer.Grant}&pin={pin}" : null);
        }
    }

    public bool AllowsUpload(IPAddress? address, string authorization)
    {
        if (address is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        lock (gate)
        {
            ClearExpired();
            return grants.TryGetValue(authorization[7..], out var grant) && grant.Address.Equals(address);
        }
    }

    public void ClearPending()
    {
        lock (gate) { inbox.Clear(); grants.Clear(); }
    }

    private void ClearExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in inbox.Where(pair => pair.Value.Expiry <= now).ToArray()) inbox.Remove(pair.Key);
        foreach (var pair in grants.Where(pair => pair.Value.Expiry <= now).ToArray()) grants.Remove(pair.Key);
        foreach (var pair in rejections.Where(pair => pair.Value.Until <= now).ToArray()) rejections.Remove(pair.Key);
    }

    public static string Code(string fingerprint, string secret)
    {
        var hash = SHA256.HashData(Convert.FromHexString(fingerprint + secret));
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 1000000).ToString("D6");
    }

    public static bool IsSecret(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static async Task<OfferResult> SendAsync(DeviceEndpoint endpoint, string path, object body, CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate?.GetCertHashString(HashAlgorithmName.SHA256) == endpoint.Pin
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await client.PostAsJsonAsync(endpoint.Origin + path, body, cancellation);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<OfferResult>(cancellation) ?? new("unreachable")
                : new("unreachable");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new("unreachable");
        }
    }
}

internal sealed record SendOffer(string Address, string Secret, string? Id);
internal sealed record OfferProof(string Id, string Secret);
