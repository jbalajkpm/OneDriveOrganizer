using System.IO.Compression;
using System.Security.Cryptography;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using Serilog;

namespace OneDriveOrganizer.Services;

public sealed class ArchiveService(IndexService index, ReportStore reports)
{
    public async Task<List<ReportRow>> Analyze(ScanOptions options, string output, CancellationToken token)
    {
        var files = await index.Refresh(options, token);
        var archives = files.Where(f => f.Extension is ".zip" or ".7z" or ".rar").ToList();
        var loose = files.Except(archives).ToList();
        var rows = new List<ReportRow>();
        var proofs = new HashSet<string>(PathPolicy.Comparer);
        var expectedProofs = new Dictionary<string, string>(PathPolicy.Comparer);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        var policy = new PathPolicy(options);
        foreach (var file in archives)
        {
            token.ThrowIfCancellationRequested();
            IndexService.AssertUnchanged(file, policy);
            file.FullHash = await HashService.Sha256(file.Path, token);
            var row = DedupService.Row(file);
            rows.Add(row);
            if (file.Extension != ".zip") { row.Status = "Unsupported"; row.Reason = "No 7z/RAR decoder installed"; continue; }
            try
            {
                using var zip = ZipFile.OpenRead(file.Path);
                var members = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                if (members.Count > 100_000 || members.Sum(e => (decimal)e.Length) > 2L * 1024 * 1024 * 1024)
                {
                    row.Status = "Unsupported"; row.Reason = "Archive exceeds 100,000 members or 2 GiB uncompressed safety limit"; continue;
                }
                int matched = 0;
                var signature = new List<string>();
                var kept = new HashSet<string>(PathPolicy.Comparer);
                bool nested = false;
                foreach (var entry in members)
                {
                    token.ThrowIfCancellationRequested();
                    nested |= Path.GetExtension(entry.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase);
                    var candidates = loose.Where(f => f.Size == entry.Length).ToList();
                    await index.FullHashes(candidates, options, token, crc: true);
                    candidates = candidates.Where(f => f.Crc32 == entry.Crc32).ToList();
                    await using var stream = entry.Open();
                    var hash = await HashService.BoundedSha256(stream, entry.Length, token);
                    signature.Add(entry.FullName + "\0" + entry.Length + "\0" + hash);
                    var match = candidates.FirstOrDefault(f => f.FullHash == hash);
                    if (match != null) { matched++; kept.Add(match.Path); expectedProofs[match.Path] = hash; }
                }
                var archiveSignature = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(signature.Order(StringComparer.Ordinal))));
                row.Status = members.Count > 0 && matched == members.Count ? "Redundant" : matched > 0 ? "Partial" : "Unique";
                row.Reason = $"{matched}/{members.Count} members match loose size + CRC32 + SHA-256; nested ZIP: {nested}";
                if (members.Count > 0 && signatures.TryGetValue(archiveSignature, out var other)) row.Reason += $"; contents duplicate archive: {other}";
                else signatures.TryAdd(archiveSignature, file.Path);
                row.KeptPath = string.Join(" | ", kept.Order(PathPolicy.Comparer));
                row.Keep = row.Status != "Redundant" || policy.Repository(file.Path) != null;
                if (policy.Repository(file.Path) != null) row.Status = "Protected";
                proofs.UnionWith(kept);
                IndexService.AssertUnchanged(file, policy);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                row.Keep = true; row.Status = "Unsupported"; row.Reason = ex.Message;
                Log.Warning("Cannot analyze {Path}: {Error}", file.Path, ex.Message);
            }
        }
        IndexService.Persist(options.Root, files);
        await reports.Write("zips", options, output, rows, token, proofs, expectedProofs);
        return rows;
    }
}
