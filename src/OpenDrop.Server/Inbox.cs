using System.Collections.Concurrent;

internal sealed class Inbox(string folder)
{
    private readonly ConcurrentQueue<Receipt> receipts = new();

    public Receipt[] RecentReceipts() => receipts.ToArray();

    // The original name is metadata only. Disk names always begin with a server-generated ID.
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 120 } && !string.IsNullOrWhiteSpace(name) &&
        name is not "." and not ".." && !name.EndsWith('.') && !name.EndsWith(' ') &&
        !name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character));

    public async Task<Receipt> SaveAsync(string name, Stream input, long limit, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString("N");
        var storedName = $"{id}_{name}";
        var temporaryPath = Path.Combine(folder, $".{id}.part");
        var finalPath = Path.Combine(folder, storedName);
        long total = 0;

        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > limit)
                        throw new UploadTooLargeException();
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
                await output.FlushAsync(cancellation);
            }

            cancellation.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
            var receipt = new Receipt(id, name, storedName, total, DateTimeOffset.UtcNow);
            receipts.Enqueue(receipt);
            if (receipts.Count > 20)
                receipts.TryDequeue(out _);
            return receipt;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

internal sealed record Receipt(string Id, string Name, string StoredName, long Bytes, DateTimeOffset ReceivedAt);
internal sealed class UploadTooLargeException : Exception { }
