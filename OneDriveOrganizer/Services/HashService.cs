using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using OneDriveOrganizer.Infrastructure;
using Serilog;

namespace OneDriveOrganizer.Services;

public sealed class HashService
{
    public const int PartialBytes = 64 * 1024;
    public static async Task<string> Sha256(string path, CancellationToken token, bool partial = false)
    {
        try
        {
            await using var stream = Open(path);
            if (!partial) return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            var buffer = new byte[PartialBytes];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count)));
        }
        catch (IOException ex)
        {
            Log.Error("Cannot read file {Path} (HRESULT 0x{HResult:X8}): {Error}", path, ex.HResult, ex.Message);
            throw;
        }
    }

    public static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static async Task<string> BoundedSha256(Stream stream, long expectedLength, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            length += read;
            if (length > expectedLength) throw new InvalidDataException("Archive member exceeds its declared length");
            hash.AppendData(buffer, 0, read);
        }
        if (length != expectedLength) throw new InvalidDataException("Truncated archive member");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static async Task<uint> Crc32(Stream stream, CancellationToken token)
    {
        uint crc = uint.MaxValue;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), token)) != 0)
                for (int i = 0; i < read; i++) crc = Table[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
            return ~crc;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    public static async Task<string> Fingerprint(string path, bool directory, CancellationToken token)
    {
        PathPolicy.AssertNoLinks(path);
        if (!directory) return await Sha256(path, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stack = new Stack<string>();
        stack.Push(path);
        var entries = new List<string>();
        while (stack.TryPop(out var folder))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(child);
                if (PathPolicy.IsLink(child) || PathPolicy.IsOffline(attrs) || PathPolicy.ProtectedName(child))
                    throw new IOException($"Repository contains a protected/offline/link entry: {child}");
                entries.Add(child);
                if (Directory.Exists(child)) stack.Push(child);
            }
        }
        foreach (var entry in entries.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(path, entry);
            var value = Directory.Exists(entry) ? "D" : await Sha256(entry, token);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative + "\0" + value + "\0"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
