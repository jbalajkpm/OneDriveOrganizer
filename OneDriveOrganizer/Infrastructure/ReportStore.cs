using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;

namespace OneDriveOrganizer.Infrastructure;

public sealed class ReportStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static CsvConfiguration CsvConfig => new(CultureInfo.InvariantCulture) { NewLine = "\r\n" };

    public static void WriteCsv(string path, IEnumerable<ReportRow> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CsvConfig);
        csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = ["O"];
        csv.WriteRecords(rows);
    }

    public static List<ReportRow> ReadCsv(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        using var csv = new CsvReader(reader, CsvConfig);
        csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().DateTimeStyle = DateTimeStyles.RoundtripKind;
        return csv.GetRecords<ReportRow>().ToList();
    }

    public static void WriteJson<T>(string path, T value, bool overwrite = true)
    {
        PathPolicy.AssertNoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Invalid JSON: {path}");

    public async Task Write(string kind, ScanOptions options, string output, List<ReportRow> rows, CancellationToken token, IEnumerable<string>? proofPaths = null, IReadOnlyDictionary<string, string>? expectedProofs = null)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output) || File.Exists(output + ".manifest.json")) throw new IOException($"Report already exists; choose a new --out: {output}");
        PathPolicy.AssertNoLinks(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var policy = new PathPolicy(options);
        var manifest = new ReportManifest { Root = policy.Root, Excludes = policy.Excludes, Kind = kind, Rows = rows, SkipOffline = options.SkipOffline, MediaAnalysisVersion = MediaService.AnalysisVersion };
        if (kind is "dupes" or "zips" or "photos" or "classify")
        {
            foreach (var row in rows.Where(r => r.Status != "Protected"))
            {
                token.ThrowIfCancellationRequested();
                policy.AssertAllowed(row.Path);
                // Fresh proof, not a cached hash: all executable reports are bound to their observed bytes.
                var proof = await HashService.Fingerprint(row.Path, row.IsDirectory, token);
                if (!row.IsDirectory)
                {
                    var info = new FileInfo(row.Path);
                    if (info.Length != row.Size || info.LastWriteTimeUtc != row.LastWriteUtc || (row.Hash.Length > 0 && row.Hash != proof))
                        throw new IOException($"Source changed while creating the report: {row.Path}");
                }
                manifest.Proofs[row.Path] = proof;
            }
            foreach (var path in proofPaths ?? [])
            {
                policy.AssertAllowed(path);
                if (!manifest.Proofs.ContainsKey(path)) manifest.Proofs[path] = await HashService.Fingerprint(path, false, token);
                if (expectedProofs != null && expectedProofs.TryGetValue(path, out var expected) && expected != manifest.Proofs[path])
                    throw new IOException($"Keeper changed while creating the report: {path}");
            }
        }
        using var store = new IndexStore(policy.Root);
        store.Register(output);
        store.Register(output + ".manifest.json");
        // Metadata is written last: incomplete reports cannot be executed.
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
        using (var csv = new CsvWriter(writer, CsvConfig))
        {
            csv.Context.TypeConverterOptionsCache.GetOptions<DateTime>().Formats = ["O"];
            csv.WriteRecords(rows);
        }
        WriteJson(output + ".manifest.json", manifest, false);
    }

    public static (ReportManifest Manifest, List<ReportRow> Rows) ReadReviewed(string path)
    {
        var manifest = ReadJson<ReportManifest>(path + ".manifest.json");
        if (manifest.Version != 1) throw new InvalidDataException("Unsupported report version");
        if (manifest.Kind is not ("dupes" or "zips" or "photos" or "classify")) throw new InvalidDataException("This report is advisory and cannot be executed");
        if (manifest.Kind is "photos" or "classify" && manifest.MediaAnalysisVersion != MediaService.AnalysisVersion)
            throw new InvalidDataException("Image-analysis engine changed; regenerate and review this photos/classify report before applying it");
        var rows = ReadCsv(path);
        var original = manifest.Rows.ToDictionary(r => r.Path, PathPolicy.Comparer);
        var seen = new HashSet<string>(PathPolicy.Comparer);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Path) || !original.TryGetValue(row.Path, out var before) ||
                (row with { Keep = before.Keep }) != before || (before.Keep && !row.Keep))
                throw new InvalidDataException("Report was altered beyond removing rows or changing Keep from false to true; regenerate it.");
        }
        return (manifest, rows);
    }
}
