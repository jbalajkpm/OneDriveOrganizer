using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services.AI;

namespace OneDriveOrganizer.Services;

public sealed class DedupService(IndexService index, ReportStore reports, MediaService media)
{
    public async Task<List<ReportRow>> Dupes(ScanOptions options, string output, string keep, string? preferFolder, CancellationToken token)
    {
        if (!new[] { "oldest", "newest", "shortest-path", "prefer-folder" }.Contains(keep)) throw new ArgumentException("Unknown keep strategy");
        if (keep == "prefer-folder" && string.IsNullOrWhiteSpace(preferFolder)) throw new ArgumentException("--keep prefer-folder requires --prefer-folder");
        var files = await index.Refresh(options, token);
        var candidates = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g.GroupBy(f => f.PartialHash).Where(h => h.Count() > 1).SelectMany(h => h)).ToList();
        await index.FullHashes(candidates, options, token);
        var rows = new List<ReportRow>();
        var policy = new PathPolicy(options);
        var companions = PairMap(files);
        int groupId = 0;
        foreach (var group in candidates.GroupBy(f => (f.Size, f.FullHash)).Where(g => g.Count() > 1))
        {
            var ordered = keep switch
            {
                "newest" => group.OrderByDescending(f => f.LastWriteUtc),
                "shortest-path" => group.OrderBy(f => f.Path.Length),
                "prefer-folder" => group.OrderBy(f => PathPolicy.Within(f.Path, Path.IsPathRooted(preferFolder!) ? preferFolder! : Path.Combine(options.Root, preferFolder!)) ? 0 : 1),
                _ => group.OrderBy(f => f.LastWriteUtc)
            };
            var winner = ordered.ThenBy(f => f.Path, PathPolicy.Comparer).First();
            groupId++;
            foreach (var file in group)
            {
                bool protect = policy.Repository(file.Path) != null || companions[file.Path].Count > 0;
                rows.Add(Row(file) with { GroupId = groupId.ToString(), Keep = file == winner || protect, KeptPath = winner.Path,
                    Status = protect ? "Protected" : "ExactDuplicate", Reason = protect ? "Repository or media companion protected" : "Size + partial SHA-256 + full SHA-256 match" });
            }
        }
        IndexService.Persist(options.Root, files);
        await reports.Write("dupes", options, output, rows, token, rows.Where(r => !r.Keep).Select(r => r.KeptPath));
        return rows;
    }

    public static ReportRow Row(FileRecord file) => new() { Path = file.Path, Size = file.Size, LastWriteUtc = file.LastWriteUtc, Hash = file.FullHash ?? "", Conflict = file.Conflict };

    public static Dictionary<string, List<FileRecord>> PairMap(List<FileRecord> files)
    {
        var result = new Dictionary<string, List<FileRecord>>(PathPolicy.Comparer);
        foreach (var group in files.GroupBy(f => Path.Combine(Path.GetDirectoryName(f.Path)!, Path.GetFileNameWithoutExtension(f.Path)), PathPolicy.Comparer))
        {
            var members = group.ToList();
            foreach (var file in members) result[file.Path] = Pair(members, file);
        }
        return result;
    }

    public static List<FileRecord> Pair(List<FileRecord> files, FileRecord file)
    {
        string stem = Path.Combine(Path.GetDirectoryName(file.Path)!, Path.GetFileNameWithoutExtension(file.Path));
        return files.Where(f => f != file && PathPolicy.Comparer.Equals(Path.Combine(Path.GetDirectoryName(f.Path)!, Path.GetFileNameWithoutExtension(f.Path)), stem) &&
            ((MediaService.Raw.Contains(f.Extension) && file.Extension is ".jpg" or ".jpeg") || (MediaService.Raw.Contains(file.Extension) && f.Extension is ".jpg" or ".jpeg") ||
            (f.Extension == ".mov" && file.Extension is ".heic" or ".heif" or ".jpg" or ".jpeg") || (file.Extension == ".mov" && f.Extension is ".heic" or ".heif" or ".jpg" or ".jpeg"))).ToList();
    }

    public async Task<List<ReportRow>> Photos(ScanOptions options, string output, int threshold, bool dropJpeg, string? ffmpeg, string? ffprobe, CancellationToken token, AiSession? ai = null)
    {
        if (threshold is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be 0..16");
        var files = await index.Refresh(options, token);
        if ((ffmpeg == null) != (ffprobe == null)) throw new ArgumentException("Provide both --ffmpeg and --ffprobe");
        if (ffmpeg != null)
            foreach (var file in files.Where(f => MediaService.Videos.Contains(f.Extension)))
                await media.VideoFrame(file, ffmpeg, ffprobe!, token);
        var photos = files.Where(f => f.PerceptualHash.HasValue).ToList();
        var companions = PairMap(files);
        var union = VisualGrouping.Group(photos, threshold, token);
        var embeddings = ai == null ? new Dictionary<string, float[]>(PathPolicy.Comparer) : await ai.IndexImages(files, options, token);
        if (ai != null) CosineGrouping.Merge(photos, embeddings, union, ai.Options.CosineThreshold, token);
        var rows = new List<ReportRow>();
        var policy = new PathPolicy(options);
        int id = 0;
        foreach (var group in Enumerable.Range(0, photos.Count).GroupBy(union.Find).Where(g => g.Count() > 1))
        {
            var members = group.Select(i => photos[i]).ToList();
            var winner = members.OrderByDescending(f => (long)f.Width * f.Height).ThenByDescending(f => f.Size).ThenByDescending(f => f.Taken.HasValue).ThenBy(f => f.Path, PathPolicy.Comparer).First();
            id++;
            foreach (var file in members)
            {
                bool paired = companions[file.Path].Count > 0;
                bool semanticMatch = ai != null && embeddings.TryGetValue(file.Path, out var vector) && embeddings.TryGetValue(winner.Path, out var keeperVector) && Vectors.Cosine(vector, keeperVector) >= ai.Options.CosineThreshold;
                bool directMatch = HashService.Hamming(file.PerceptualHash!.Value, winner.PerceptualHash!.Value) <= threshold || semanticMatch;
                bool protect = paired || policy.Repository(file.Path) != null || MediaService.Videos.Contains(file.Extension) || !directMatch;
                rows.Add(Row(file) with { GroupId = "P" + id, Keep = file == winner || protect, KeptPath = winner.Path,
                    Status = protect ? "Protected" : file == winner ? "BestQuality" : "LowerQualityCopy",
                    AiProvider = ai == null ? "" : "LocalCLIP",
                    Reason = $"dHash/CLIP group; CLIP keeper match={semanticMatch}; {file.Width}x{file.Height}; {file.MediaStatus}; " +
                        (file.Path.Contains("whatsapp", StringComparison.OrdinalIgnoreCase) || file.Path.Contains("-WA", StringComparison.OrdinalIgnoreCase) ? "Possible WhatsApp recompression; " :
                         file.Path.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ? "Possible screenshot of photo; " : "") +
                        (paired ? "RAW/Live Photo pair preserved" : !directMatch ? "Transitive group member is outside keeper threshold" : "Visual similarity is not proof of identical content; review") });
            }
        }
        var reported = rows.Select(r => r.Path).ToHashSet(PathPolicy.Comparer);
        foreach (var file in files.Where(f => MediaService.Images.Contains(f.Extension) || MediaService.Videos.Contains(f.Extension)))
        {
            if (reported.Contains(file.Path)) continue;
            rows.Add(Row(file) with { Status = file.PerceptualHash.HasValue ? "Unique" : "Unsupported", Reason = file.MediaStatus });
        }
        if (dropJpeg)
        {
            foreach (var row in rows.Where(r => Path.GetExtension(r.Path).ToLowerInvariant() is ".jpg" or ".jpeg"))
            {
                var file = files.Single(f => PathPolicy.Comparer.Equals(f.Path, row.Path));
                var pair = companions[file.Path];
                var raw = pair.FirstOrDefault(f => MediaService.Raw.Contains(f.Extension));
                if (raw != null && !pair.Any(f => f.Extension == ".mov") && policy.Repository(file.Path) == null)
                {
                    row.Keep = false; row.KeptPath = raw.Path; row.Status = "JpegWithRaw";
                    row.Reason = "Explicit --drop-jpeg-when-raw; rendered JPEG and RAW are not equivalent";
                }
            }
        }
        IndexService.Persist(options.Root, files);
        await reports.Write("photos", options, output, rows, token, rows.Where(r => !r.Keep).Select(r => r.KeptPath));
        return rows;
    }

    public async Task<List<ReportRow>> Folders(ScanOptions options, string output, CancellationToken token)
    {
        var files = await index.Refresh(options, token);
        await index.FullHashes(files, options, token);
        var trees = new Dictionary<string, Dictionary<string, int>>(PathPolicy.Comparer);
        foreach (var file in files)
            for (var dir = Path.GetDirectoryName(file.Path); dir != null && PathPolicy.Within(dir, options.Root) && !PathPolicy.Comparer.Equals(dir, PathPolicy.Normalize(options.Root)); dir = Path.GetDirectoryName(dir))
            {
                if (!trees.TryGetValue(dir, out var hashes)) trees[dir] = hashes = new();
                hashes[file.FullHash!] = hashes.GetValueOrDefault(file.FullHash!) + 1;
            }
        var rows = new List<ReportRow>();
        var keys = trees.Keys.Order(PathPolicy.Comparer).ToList();
        for (int i = 0; i < keys.Count; i++)
            for (int j = i + 1; j < keys.Count; j++)
            {
                token.ThrowIfCancellationRequested();
                if (PathPolicy.Within(keys[i], keys[j]) || PathPolicy.Within(keys[j], keys[i])) continue;
                bool left = Subset(trees[keys[i]], trees[keys[j]]), right = Subset(trees[keys[j]], trees[keys[i]]);
                if (!left && !right) continue;
                rows.Add(new ReportRow { Path = left ? keys[i] : keys[j], KeptPath = left ? keys[j] : keys[i], IsDirectory = true,
                    Status = left && right ? "DuplicateTree" : "StrictSubset", Reason = "Content multiset of indexed files only; excluded/offline entries are not compared. Advisory, never executable." });
            }
        IndexService.Persist(options.Root, files);
        await reports.Write("folders", options, output, rows, token);
        return rows;
    }

    private static bool Subset(Dictionary<string, int> a, Dictionary<string, int> b) => a.All(kv => b.GetValueOrDefault(kv.Key) >= kv.Value);
}

public sealed class UnionFind(int count)
{
    private readonly int[] parents = Enumerable.Range(0, count).ToArray();
    private readonly int[] ranks = new int[count];
    public int Find(int x) { while (parents[x] != x) { parents[x] = parents[parents[x]]; x = parents[x]; } return x; }
    public void Join(int a, int b)
    {
        a = Find(a); b = Find(b); if (a == b) return;
        if (ranks[a] < ranks[b]) (a, b) = (b, a);
        parents[b] = a; if (ranks[a] == ranks[b]) ranks[a]++;
    }
}
