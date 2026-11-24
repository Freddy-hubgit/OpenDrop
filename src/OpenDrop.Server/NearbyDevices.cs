using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal sealed class NearbyDevices(string? transferOrigin, string? certificatePin) : BackgroundService
{
    private const int DiscoveryPort = 5189;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.79.68");
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<IPAddress, Peer> peers = new();
    private volatile bool visible;
    private volatile string? error;

    public bool IsVisible => visible;

    public void SetVisibility(bool enabled)
    {
        visible = enabled;
        if (!enabled) peers.Clear();
    }

    public object Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
        foreach (var peer in peers)
            if (peer.Value.SeenAt < cutoff) peers.TryRemove(peer);
        return new { visible, error, devices = visible
            ? peers.Values.Where(peer => peer.SeenAt >= cutoff).OrderBy(peer => peer.Name).ToArray() : [] };
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var announcement = JsonSerializer.SerializeToUtf8Bytes(
            new Beacon("OpenDrop/1", instance, Environment.MachineName, transferOrigin, certificatePin));
        while (!stopping.IsCancellationRequested)
        {
            if (!visible)
            {
                await Task.Delay(400, stopping);
                continue;
            }

            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.ExclusiveAddressUse = false;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                udp.JoinMulticastGroup(Group, 1);
                error = null;
                var destination = new IPEndPoint(Group, DiscoveryPort);
                while (visible && !stopping.IsCancellationRequested)
                {
                    await udp.SendAsync(announcement, destination, stopping);
                    using var interval = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    interval.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        while (visible) Record(await udp.ReceiveAsync(interval.Token));
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
                }
            }
            catch (SocketException)
            {
                error = "Die Gerätesuche kann UDP-Port 5189 nicht verwenden.";
                await Task.Delay(2000, stopping);
            }
        }
    }

    private void Record(UdpReceiveResult message)
    {
        if (message.Buffer.Length > 768 || !IsPrivate(message.RemoteEndPoint.Address)) return;
        try
        {
            var beacon = JsonSerializer.Deserialize<Beacon>(message.Buffer);
            if (beacon?.Protocol != "OpenDrop/1" || beacon.Id == instance || !Guid.TryParseExact(beacon.Id, "N", out _) ||
                beacon.Name is not { Length: > 0 and <= 64 } || beacon.Name.Any(char.IsControl)) return;

            var origin = Uri.TryCreate(beacon.Origin, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Port >= 1024 &&
                uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.HostNameType == UriHostNameType.Dns || uri.Host == message.RemoteEndPoint.Address.ToString())
                ? uri.GetLeftPart(UriPartial.Authority) : null;

            var address = message.RemoteEndPoint.Address;
            if (peers.Count < 128 || peers.ContainsKey(address))
                peers[address] = new(beacon.Name, address.ToString(), origin,
                    origin is not null && uri!.Host == address.ToString() && ConnectionRequests.IsSecret(beacon.Pin) ? beacon.Pin : null,
                    DateTimeOffset.UtcNow);
        }
        catch (JsonException) { }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }

    private sealed record Beacon(string Protocol, string Id, string Name, string? Origin, string? Pin);
    private sealed record Peer(string Name, string Address, string? Origin, string? Pin, DateTimeOffset SeenAt);

    public DeviceEndpoint? GetEndpoint(string? address)
    {
        if (!visible || !IPAddress.TryParse(address, out var ip) ||
            !peers.TryGetValue(ip, out var peer) || peer.SeenAt < DateTimeOffset.UtcNow.AddSeconds(-10) ||
            peer.Origin is null || peer.Pin is null)
            return null;
        return new(peer.Origin, peer.Pin);
    }
}

internal sealed record NearbyVisibility(bool Enabled);
