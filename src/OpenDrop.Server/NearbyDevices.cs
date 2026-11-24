using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal sealed class NearbyDevices(string? transferOrigin, string? certificatePin, FriendBook friends) : BackgroundService
{
    private const int DiscoveryPort = 5189;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.79.68");
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<IPAddress, Peer> nearby = new();
    private readonly ConcurrentDictionary<IPAddress, Peer> known = new();
    private volatile bool visible;
    private volatile bool friendsVisible;
    private volatile string? error;

    public bool IsVisible => visible;
    public bool FriendsVisible => friendsVisible;
    public bool IsDiscoverable => visible || friendsVisible;

    public void SetVisibility(bool enabled)
    {
        visible = enabled;
        if (!enabled) nearby.Clear();
    }

    public void SetFriendsVisibility(bool enabled)
    {
        friendsVisible = enabled;
        if (!enabled) known.Clear();
    }

    public object Snapshot()
    {
        var saved = friends.Snapshot();
        var devices = Available(saved).OrderBy(peer => peer.Name)
            .Select(peer => new { peer.Name, peer.Address, peer.Origin, peer.Pin, isFriend = peer.FriendId is not null });
        return new
        {
            visible, friendsVisible, error, devices,
            friends = saved.OrderBy(friend => friend.Name).Select(friend => new { friend.Id, friend.Name })
        };
    }

    public DeviceEndpoint? GetEndpoint(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return null;
        var saved = friends.Snapshot();
        var peer = Available(saved).FirstOrDefault(item => item.Address == ip.ToString());
        if (peer?.Origin is null || peer.Pin is null) return null;
        return new(peer.Origin, peer.Pin, peer.Name, saved.FirstOrDefault(friend => friend.Id == peer.FriendId)?.Key);
    }

    private IEnumerable<Peer> Available(FriendDevice[] saved)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
        foreach (var map in new[] { nearby, known })
            foreach (var entry in map)
                if (entry.Value.SeenAt < cutoff) map.TryRemove(entry);
        IEnumerable<Peer> publicPeers = visible ? nearby.Values : [];
        IEnumerable<Peer> friendsOnly = friendsVisible
            ? known.Values.Where(peer => saved.Any(friend => friend.Id == peer.FriendId)) : [];
        return friendsOnly.Concat(publicPeers).DistinctBy(peer => peer.Address);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            if (!IsDiscoverable)
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
                while (IsDiscoverable && !stopping.IsCancellationRequested)
                {
                    var beacon = new Beacon("OpenDrop/1", instance, Environment.MachineName,
                        transferOrigin, certificatePin, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var announcement = JsonSerializer.SerializeToUtf8Bytes(beacon);
                    if (visible) await udp.SendAsync(announcement, destination, stopping);
                    if (friendsVisible)
                        foreach (var friend in friends.Snapshot())
                            await udp.SendAsync(FriendBeacon.Protect(friend, announcement), destination, stopping);

                    using var interval = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    interval.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        while (IsDiscoverable) Record(await udp.ReceiveAsync(interval.Token));
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
        var address = message.RemoteEndPoint.Address;
        if (message.Buffer.Length > 768 || !IsPrivate(address)) return;
        var payload = message.Buffer;
        string? friendId = null;
        if (FriendBeacon.IsPrivateBeacon(payload))
        {
            if (!friendsVisible) return;
            foreach (var friend in friends.Snapshot())
            {
                var decrypted = FriendBeacon.Open(friend, payload);
                if (decrypted is null) continue;
                payload = decrypted;
                friendId = friend.Id;
                break;
            }
            if (friendId is null) return;
        }
        else if (!visible) return;

        try
        {
            var beacon = JsonSerializer.Deserialize<Beacon>(payload);
            if (beacon?.Protocol != "OpenDrop/1" || beacon.Id == instance || !Guid.TryParseExact(beacon.Id, "N", out _) ||
                beacon.Name is not { Length: > 0 and <= 64 } || beacon.Name.Any(char.IsControl)) return;
            var now = DateTimeOffset.UtcNow;
            var nowSeconds = now.ToUnixTimeSeconds();
            if (friendId is not null && (beacon.SentAt < nowSeconds - 15 || beacon.SentAt > nowSeconds + 15)) return;

            var origin = Uri.TryCreate(beacon.Origin, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Port >= 1024 &&
                uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.HostNameType == UriHostNameType.Dns || uri.Host == address.ToString())
                ? uri.GetLeftPart(UriPartial.Authority) : null;

            var map = friendId is null ? nearby : known;
            if (map.Count < 128 || map.ContainsKey(address))
                map[address] = new(beacon.Name, address.ToString(), origin,
                    origin is not null && uri!.Host == address.ToString() && ConnectionRequests.IsSecret(beacon.Pin) ? beacon.Pin : null,
                    now, friendId);
        }
        catch (JsonException) { }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168));
    }

    private sealed record Beacon(string Protocol, string Id, string Name, string? Origin, string? Pin, long SentAt);
    private sealed record Peer(string Name, string Address, string? Origin, string? Pin, DateTimeOffset SeenAt, string? FriendId);
}

internal sealed record NearbyVisibility(bool Enabled);
