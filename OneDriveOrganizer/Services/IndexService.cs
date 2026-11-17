using System.Collections.Concurrent;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using Serilog;

namespace OneDriveOrganizer.Services;

public sealed class IndexService(MediaService media)
{
    public async Task<List<FileRecord>> Refresh(ScanOptions options, CancellationToken token)
    {
        if (options.Parallelism is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options), "Parallelism must be 1..64");
        var policy = new PathPolicy(options);
        using var store = new IndexStore(policy.Root);
        var previous = store.Load();
        var artifacts = store.Artifacts();
        var files = policy.Enumerate(token).Where(p => !artifacts.Contains(p)).ToList();
        var results = new ConcurrentBag<FileRecord>();
        int done = 0, reused = 0, refreshedMedia = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = options.Parallelism }, async (path, ct) =>
        {
            policy.AssertAllowed(path);
            var info = new FileInfo(path);
            if (previous.TryGetValue(path, out var old) && old.Size == info.Length && old.LastWriteUtc == info.LastWriteTimeUtc)
            {
                old.Attributes = (int)info.Attributes;
                if ((MediaService.Images.Contains(old.Extension) || MediaService.Videos.Contains(old.Extension)) && old.MediaAnalysisVersion != MediaService.AnalysisVersion)
                {
                    await media.Read(old, ct);
                    AssertUnchanged(old, policy);
                    Interlocked.Increment(ref refreshedMedia);
                }
                results.Add(old);
                Interlocked.Increment(ref reused);
            }
            else
            {
                var file = new FileRecord { Path = path, Size = info.Length, CreatedUtc = info.CreationTimeUtc, LastWriteUtc = info.LastWriteTimeUtc,
                    Attributes = (int)info.Attributes, Extension = info.Extension.ToLowerInvariant(), Conflict = PathPolicy.Conflict(path) };
                file.PartialHash = await HashService.Sha256(path, ct, partial: true);
                await media.Read(file, ct);
                AssertUnchanged(file, policy);
                results.Add(file);
            }
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0 || n == files.Count) Log.Information("Index {Done}/{Total}", n, files.Count);
        });
        var sorted = results.OrderBy(f => f.Path, PathPolicy.Comparer).ToList();
        token.ThrowIfCancellationRequested();
        store.Save(sorted);
        Log.Information("Indexed {Count}; reused {Reused}; refreshed media analysis {Refreshed}; offline placeholders skipped {Skipped}", sorted.Count, reused, refreshedMedia, policy.SkippedOffline);
        return sorted;
    }

    public static void AssertUnchanged(FileRecord file, PathPolicy policy)
    {
        policy.AssertAllowed(file.Path);
        var info = new FileInfo(file.Path);
        if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.LastWriteUtc) throw new IOException($"File changed during scan; regenerate report: {file.Path}");
    }

    public async Task FullHashes(IEnumerable<FileRecord> files, ScanOptions options, CancellationToken token, bool crc = false)
    {
        var list = files.DistinctBy(f => f.Path, PathPolicy.Comparer).ToList();
        var policy = new PathPolicy(options);
        int done = 0;
        await Parallel.ForEachAsync(list, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = options.Parallelism }, async (file, ct) =>
        {
            AssertUnchanged(file, policy);
            file.FullHash ??= await HashService.Sha256(file.Path, ct);
            if (crc && file.Crc32 == null)
            {
                await using var stream = HashService.Open(file.Path);
                file.Crc32 = await HashService.Crc32(stream, ct);
            }
            AssertUnchanged(file, policy);
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0 || n == list.Count) Log.Information("Hash {Done}/{Total}", n, list.Count);
        });
    }

    public static void Persist(string root, List<FileRecord> files)
    {
        using var store = new IndexStore(root);
        store.Save(files);
    }
}
