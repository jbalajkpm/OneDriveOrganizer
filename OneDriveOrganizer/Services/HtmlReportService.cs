using System.Globalization;
using System.Net;
using System.Text;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;

namespace OneDriveOrganizer.Services;

public sealed class HtmlReportService(IndexService index)
{
    public async Task Write(ScanOptions options, string output, CancellationToken token)
    {
        var files = await index.Refresh(options, token);
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("HTML output already exists; choose a new --out");
        PathPolicy.AssertNoLinks(output);
        using var store = new IndexStore(options.Root);
        var artifacts = store.Artifacts();
        var manifests = artifacts.Where(p => p.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).OrderByDescending(File.GetLastWriteTimeUtc).Select(ReportStore.ReadJson<ReportManifest>).ToList();
        var plans = manifests.FirstOrDefault(m => m.Kind == "classify")?.Rows ?? [];
        long removedBytes = 0;
        foreach (var path in artifacts.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".suggested-rules.json", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
        {
            var journal = ReportStore.ReadJson<UndoJournal>(path);
            foreach (var entry in journal.Entries.Where(e => e.State == "Applied" && e.Action is "Trash" or "Recycle"))
                if (File.Exists(entry.Destination)) removedBytes += new FileInfo(entry.Destination).Length;
        }
        string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>OneDriveOrganizer</title><style>body{font:16px system-ui;max-width:1100px;margin:2em auto;padding:1em}table{border-collapse:collapse;width:100%}td,th{padding:.5em;border-bottom:1px solid #ccc;text-align:left;overflow-wrap:anywhere}svg{max-width:400px}small{color:#555}</style><h1>OneDriveOrganizer</h1>");
        html.Append($"<p>Root: {E(options.Root)}</p><p>{files.Count:N0} indexed files; {files.Sum(f => f.Size):N0} bytes. Removed from original locations: {removedBytes:N0} bytes.</p><p><strong>Physical space reclaimed: 0 bytes guaranteed.</strong> Local _Trash backups and Windows Recycle Bin still consume storage. No backup is automatically purged.</p>");
        html.Append("<h2>Duplicates by type (reviewed report history)</h2><table><tr><th>Report</th><th>Suggested removals</th><th>Bytes</th></tr>");
        foreach (var group in manifests.Where(m => m.Kind is "dupes" or "photos" or "zips").GroupBy(m => m.Kind))
        {
            var candidates = group.SelectMany(m => m.Rows).Where(r => !r.Keep).DistinctBy(r => r.Path, PathPolicy.Comparer).ToList();
            html.Append($"<tr><td>{E(group.Key)}</td><td>{candidates.Count}</td><td>{candidates.Sum(r => r.Size):N0}</td></tr>");
        }
        html.Append("</table><h2>Categories (latest available plans, not a rescan of content)</h2>");
        var categories = plans.GroupBy(r => r.Category).OrderByDescending(g => g.Count()).ToList();
        double angle = -Math.PI / 2;
        html.Append("<svg viewBox=\"0 0 240 240\" role=\"img\" aria-label=\"Category distribution\">");
        for (int i = 0; i < categories.Count; i++)
        {
            double fraction = (double)categories[i].Count() / Math.Max(1, plans.Count), end = angle + fraction * Math.Tau;
            string color = $"hsl({i * 137 % 360},60%,55%)";
            if (fraction == 1) html.Append($"<circle cx=\"120\" cy=\"120\" r=\"100\" fill=\"{color}\"/>");
            else html.Append(FormattableString.Invariant($"<path d=\"M120,120 L{120 + 100 * Math.Cos(angle):F3},{120 + 100 * Math.Sin(angle):F3} A100,100 0 {(fraction > .5 ? 1 : 0)},1 {120 + 100 * Math.Cos(end):F3},{120 + 100 * Math.Sin(end):F3} Z\" fill=\"{color}\"><title>{E(categories[i].Key)}</title></path>"));
            angle = end;
        }
        html.Append("</svg><table><tr><th>Category</th><th>Items</th><th>Bytes</th></tr>");
        foreach (var category in categories) html.Append($"<tr><td>{E(category.Key)}</td><td>{category.Count()}</td><td>{category.Sum(r => r.Size):N0}</td></tr>");
        html.Append("</table>");
        foreach (var section in new[] { ("_Review items", plans.Where(r => r.Status == "Review")), ("Sensitive items", plans.Where(r => r.Sensitive)) })
        {
            html.Append($"<h2>{section.Item1}</h2><ul>");
            foreach (var row in section.Item2) html.Append($"<li>{E(row.Path)} - {E(row.Reason)}</li>");
            html.Append("</ul>");
        }
        html.Append("<h2>OneDrive conflict candidates</h2><ul>");
        foreach (var file in files.Where(f => f.Conflict.Length > 0)) html.Append($"<li>{E(file.Path)} ({E(file.Conflict)})</li>");
        html.Append("</ul><h2>Top 20 largest files</h2><table><tr><th>Path</th><th>Bytes</th></tr>");
        foreach (var file in files.OrderByDescending(f => f.Size).Take(20)) html.Append($"<tr><td>{E(file.Path)}</td><td>{file.Size:N0}</td></tr>");
        html.Append("</table><h2>Oldest/stalest folders (latest file modification)</h2><table><tr><th>Folder</th><th>Latest change UTC</th></tr>");
        foreach (var folder in files.GroupBy(f => Path.GetDirectoryName(f.Path)!).OrderBy(g => g.Max(f => f.LastWriteUtc)).Take(20))
            html.Append($"<tr><td>{E(folder.Key)}</td><td>{folder.Max(f => f.LastWriteUtc).ToString("O", CultureInfo.InvariantCulture)}</td></tr>");
        html.Append("</table><small>Reports contain personal filenames and sensitive classifications. Keep this file private. No remote resources or geocoding are used.</small></html>");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(html.ToString().AsMemory(), token);
        store.Register(output);
    }
}
