using System.Security.Cryptography;
using System.Text.Json;

internal sealed record FriendDevice(string Name, string Key)
{
    public string Id => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(Key)));
}
internal sealed record FriendVisibility(bool Enabled);
internal sealed record RemoveFriend(string Id);

internal sealed class FriendBook
{
    private readonly object gate = new();
    private readonly string path;
    private List<FriendDevice> devices;

    public FriendBook(string directory)
    {
        path = Path.Combine(directory, "friends.json");
        devices = File.Exists(path)
            ? (JsonSerializer.Deserialize<List<FriendDevice>>(File.ReadAllText(path)) ?? [])
                .Where(friend => friend is not null && ConnectionRequests.IsSecret(friend.Key) &&
                    friend.Name is { Length: > 0 and <= 64 } && !friend.Name.Any(char.IsControl))
                .DistinctBy(friend => friend.Key).Take(32).ToList()
            : [];
    }

    public FriendDevice[] Snapshot()
    {
        lock (gate) return devices.ToArray();
    }

    public bool Contains(string? key)
    {
        if (!ConnectionRequests.IsSecret(key)) return false;
        var bytes = Convert.FromHexString(key!);
        lock (gate) return devices.Any(friend =>
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(friend.Key), bytes));
    }

    public bool Add(string name, string requestSecret)
    {
        var key = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(requestSecret), "OpenDrop friend key v1"u8));
        lock (gate)
        {
            if (devices.Any(friend => friend.Key == key)) return true;
            if (devices.Count >= 32) return false;
            return Save([.. devices, new(name, key)]);
        }
    }

    public bool Remove(string id)
    {
        lock (gate) return Save(devices.Where(friend => friend.Id != id).ToList());
    }

    private bool Save(List<FriendDevice> next)
    {
        var temporary = path + $".{Guid.NewGuid():N}.part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, path, overwrite: true);
            devices = next;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal static class FriendBeacon
{
    private const byte Version = 0xF1;
    private const int Overhead = 29;

    public static byte[] Protect(FriendDevice friend, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[Overhead + payload.Length];
        packet[0] = Version;
        RandomNumberGenerator.Fill(packet.AsSpan(1, 12));
        using var cipher = new AesGcm(Convert.FromHexString(friend.Key), 16);
        cipher.Encrypt(packet.AsSpan(1, 12), payload, packet.AsSpan(Overhead), packet.AsSpan(13, 16), packet.AsSpan(0, 1));
        return packet;
    }

    public static bool IsPrivateBeacon(byte[] packet) => packet is [Version, ..];

    public static byte[]? Open(FriendDevice friend, byte[] packet)
    {
        if (!IsPrivateBeacon(packet) || packet.Length <= Overhead) return null;
        var payload = new byte[packet.Length - Overhead];
        using var cipher = new AesGcm(Convert.FromHexString(friend.Key), 16);
        try
        {
            cipher.Decrypt(packet.AsSpan(1, 12), packet.AsSpan(Overhead), packet.AsSpan(13, 16), payload, packet.AsSpan(0, 1));
            return payload;
        }
        catch (AuthenticationTagMismatchException) { return null; }
    }
}
