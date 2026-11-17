using OneDriveOrganizer.Models;

namespace OneDriveOrganizer.Services;

public static class VisualGrouping
{
    public static UnionFind Group(IReadOnlyList<FileRecord> photos, int threshold, CancellationToken token)
    {
        if (threshold is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(threshold));
        var union = new UnionFind(photos.Count);
        var bands = new Dictionary<(int Band, ulong Bits), List<int>>();
        var identical = new Dictionary<ulong, int>();
        for (int i = 0; i < photos.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ulong hash = photos[i].PerceptualHash ?? throw new ArgumentException("Visual grouping requires decoded hashes");
            if (!MediaService.Videos.Contains(photos[i].Extension))
            {
                if (identical.TryGetValue(hash, out var same)) { union.Join(i, same); continue; }
                identical.Add(hash, i);
            }
            IEnumerable<int> possible;
            if (photos.Count <= 20_000) possible = Enumerable.Range(0, i);
            else
            {
                // Distance <= t guarantees an identical one of t+1 disjoint bands.
                // A single prefix bucket would miss valid cross-prefix neighbours.
                var candidates = new HashSet<int>();
                for (int b = 0; b <= threshold; b++)
                {
                    int start = b * 64 / (threshold + 1), end = (b + 1) * 64 / (threshold + 1);
                    ulong mask = end - start == 64 ? ulong.MaxValue : (1UL << (end - start)) - 1;
                    var key = (b, (hash >> start) & mask);
                    if (!bands.TryGetValue(key, out var bucket)) bands[key] = bucket = [];
                    candidates.UnionWith(bucket);
                    bucket.Add(i);
                }
                possible = candidates;
            }
            foreach (int j in possible)
            {
                bool videoA = MediaService.Videos.Contains(photos[i].Extension), videoB = MediaService.Videos.Contains(photos[j].Extension);
                if (videoA != videoB || (videoA && (photos[i].Duration == null || photos[j].Duration == null || Math.Abs(photos[i].Duration!.Value - photos[j].Duration!.Value) > 1))) continue;
                if (HashService.Hamming(hash, photos[j].PerceptualHash!.Value) <= threshold) union.Join(i, j);
            }
        }
        return union;
    }
}
