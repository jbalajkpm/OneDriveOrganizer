using System.Runtime.InteropServices;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services.AI;
using Serilog;

namespace OneDriveOrganizer.Services;

public sealed class ExecutionService
{
    public const string Banner = "SAFETY: OneDrive personal Recycle Bin retains deleted files for 30 days; review the report and retain your local undo backups.";

    public async Task Apply(string reportPath, bool organize, bool execute, bool reviewed, bool recycle, bool copy, string? journalPath, CancellationToken token, bool rename = false)
    {
        Console.WriteLine(Banner);
        if (execute && !reviewed) throw new ArgumentException("Execution requires --reviewed and --no-dry-run after inspecting the CSV");
        var (manifest, rows) = ReportStore.ReadReviewed(reportPath);
        if (rename && (!organize || copy)) throw new ArgumentException("Rename requires move semantics");
        if (organize ? manifest.Kind != "classify" : manifest.Kind is not ("dupes" or "zips" or "photos")) throw new InvalidDataException("Wrong report kind for command");
        var policy = new PathPolicy(new(manifest.Root, manifest.Excludes, manifest.SkipOffline));
        var selected = rows.Where(r => rename ? !r.IsDirectory && r.Status is "Move" or "AlreadyPlaced" && r.SuggestedFileName.Length > 0 : organize ? r.Status == "Move" : !r.Keep).ToList();
        if (rename) selected = selected.Select(r => r with { TargetPath = AiSchema.RenameTarget(r.Path, r.SuggestedFileName) }).Where(r => !PathPolicy.Comparer.Equals(r.Path, r.TargetPath)).ToList();
        if (selected.Any(r => r.Status == "Protected" || (!organize && r.IsDirectory))) throw new InvalidDataException("Protected/advisory rows cannot execute");
        using var store = new IndexStore(policy.Root);
        var artifacts = store.Artifacts();
        foreach (var row in selected)
        {
            policy.AssertAllowed(row.Path);
            if (artifacts.Contains(row.Path) || (row.IsDirectory && artifacts.Any(a => PathPolicy.Within(a, row.Path)))) throw new IOException("An application report/journal is inside the source");
            if ((!organize || rename) && policy.Repository(row.Path) != null) throw new IOException("Never split a repository");
            if (organize)
            {
                policy.AssertAllowed(row.TargetPath);
                if (artifacts.Contains(row.TargetPath)) throw new IOException("Target is an application artifact");
                if (row.IsDirectory && (PathPolicy.Within(row.TargetPath, row.Path) || policy.Excludes.Any(p => PathPolicy.Within(p, row.Path)))) throw new IOException("Unsafe repository move");
            }
        }
        var removed = selected.Select(r => r.Path).ToHashSet(PathPolicy.Comparer);
        if (organize)
        {
            foreach (var row in selected.Where(r => !r.IsDirectory))
                foreach (var companion in Companions(row.Path))
                {
                    var other = selected.FirstOrDefault(r => PathPolicy.Comparer.Equals(r.Path, companion.Path));
                    if (other == null || !PathPolicy.Comparer.Equals(Path.GetDirectoryName(row.TargetPath), Path.GetDirectoryName(other.TargetPath)) || (rename && !PathPolicy.Comparer.Equals(Path.GetFileNameWithoutExtension(row.TargetPath), Path.GetFileNameWithoutExtension(other.TargetPath))))
                        throw new IOException($"Plan would split a media pair; include both companions: {row.Path}");
                }
        }
        if (!organize && selected.Any(r => manifest.Kind != "zips" && removed.Contains(r.KeptPath))) throw new IOException("A keeper is also scheduled for deletion");
        // Validate every proof, including loose files backing a redundant archive, before doing any work.
        foreach (var proof in manifest.Proofs)
        {
            token.ThrowIfCancellationRequested();
            policy.AssertAllowed(proof.Key);
            bool directory = manifest.Rows.Any(r => r.IsDirectory && PathPolicy.Comparer.Equals(r.Path, proof.Key));
            if (await HashService.Fingerprint(proof.Key, directory, token) != proof.Value) throw new IOException($"Report is stale: {proof.Key}");
        }
        foreach (var row in selected)
        {
            if (!manifest.Proofs.ContainsKey(row.Path)) throw new InvalidDataException("Source proof is missing");
            if (!organize && manifest.Kind != "zips" && (!manifest.Proofs.ContainsKey(row.KeptPath) || !File.Exists(row.KeptPath))) throw new InvalidDataException("Keeper proof is missing");
            if (!organize && row.Status == "ExactDuplicate" && manifest.Proofs[row.Path] != manifest.Proofs[row.KeptPath]) throw new InvalidDataException("Exact duplicate proof mismatch");
            if (!organize && row.Status != "JpegWithRaw" && Companions(row.Path).Count > 0) throw new IOException($"A media companion protects {row.Path}");
        }
        var reserved = new HashSet<string>(PathPolicy.Comparer);
        var targets = new Dictionary<string, string>(PathPolicy.Comparer);
        if (organize)
        {
            // Resolve companion collisions as a unit, preserving matching basenames.
            foreach (var group in selected.GroupBy(r => Path.Combine(Path.GetDirectoryName(r.Path)!, Path.GetFileNameWithoutExtension(r.Path)), PathPolicy.Comparer))
            {
                var members = group.ToList();
                bool companion = members.Count > 1 && members.All(r => MediaService.Images.Contains(Path.GetExtension(r.Path)) || MediaService.Videos.Contains(Path.GetExtension(r.Path)));
                string? suffix = companion && members.Any(r => Exists(r.TargetPath) || reserved.Contains(r.TargetPath)) ? "_" + manifest.Proofs[members[0].Path][..8] : null;
                int attempt = 0;
                while (companion && suffix != null && members.Any(r => Exists(Suffix(r.TargetPath, suffix + (attempt == 0 ? "" : "_" + attempt))) || reserved.Contains(Suffix(r.TargetPath, suffix + (attempt == 0 ? "" : "_" + attempt))))) attempt++;
                foreach (var row in members)
                {
                    var proposed = suffix == null ? row.TargetPath : Suffix(row.TargetPath, suffix + (attempt == 0 ? "" : "_" + attempt));
                    var target = Collision(proposed, manifest.Proofs[row.Path], reserved);
                    if (selected.Any(r => PathPolicy.Within(target, r.Path))) throw new IOException("Target intersects another source; regenerate a non-overlapping plan");
                    targets[row.Path] = target;
                }
            }
        }
        foreach (var row in selected) Console.WriteLine(organize ? $"{(copy ? "COPY" : "MOVE")} {row.Path} -> {targets[row.Path]}" : $"RECYCLE {row.Path}; KEEP {row.KeptPath}");
        Console.WriteLine($"{(execute ? "APPLY" : "DRY RUN")}: {selected.Count} actions; {selected.Sum(r => r.Size):N0} bytes. Review rows are not moved.");
        if (!execute || selected.Count == 0) { if (organize) Summary(rows); return; }
        journalPath = Path.GetFullPath(journalPath ?? Path.Combine(policy.Root, ".odo", "undo-" + Guid.NewGuid().ToString("N") + ".json"));
        if (Exists(journalPath) || selected.Any(r => PathPolicy.Within(journalPath, r.Path)) || targets.Values.Any(p => PathPolicy.Comparer.Equals(p, journalPath))) throw new IOException("Undo journal already exists or intersects a source/target");
        PathPolicy.AssertNoLinks(journalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        store.Register(journalPath);
        var journal = new UndoJournal { Root = policy.Root, Excludes = policy.Excludes };
        ReportStore.WriteJson(journalPath, journal, false);
        Log.Information("Undo journal: {Path}", journalPath);
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            policy.AssertAllowed(row.Path);
            if (await HashService.Fingerprint(row.Path, row.IsDirectory, token) != manifest.Proofs[row.Path]) throw new IOException($"Source changed: {row.Path}");
            if (!organize)
            {
                // Recheck keeper evidence immediately before removing its source; do not rely only on batch preflight.
                foreach (var proof in manifest.Proofs.Where(p => !removed.Contains(p.Key)))
                {
                    policy.AssertAllowed(proof.Key);
                    if (await HashService.Fingerprint(proof.Key, false, token) != proof.Value) throw new IOException($"Keeper changed: {proof.Key}");
                }
            }
            string destination = organize ? targets[row.Path] : TrashPath(policy.Root, row.Path);
            policy.AssertAllowed(destination, allowInternal: !organize);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var entry = new UndoEntry { Original = row.Path, Destination = destination, IsDirectory = row.IsDirectory,
                Action = organize ? copy ? "Copy" : "Move" : "Trash", Hash = manifest.Proofs[row.Path],
                CreatedUtc = row.IsDirectory ? Directory.GetCreationTimeUtc(row.Path) : File.GetCreationTimeUtc(row.Path),
                LastWriteUtc = row.IsDirectory ? Directory.GetLastWriteTimeUtc(row.Path) : File.GetLastWriteTimeUtc(row.Path) };
            journal.Entries.Add(entry);
            ReportStore.WriteJson(journalPath, journal);
            bool useRecycle = !organize && recycle && OperatingSystem.IsWindows() && row.Path.Length < 240 && !row.Path.StartsWith(@"\\", StringComparison.Ordinal);
            if (!organize && recycle && OperatingSystem.IsWindows() && !useRecycle) Log.Warning("Recycle API cannot safely handle this path; using reversible _Trash: {Path}", row.Path);
            if (copy && organize || useRecycle)
            {
                // Copy through a journaled staging path: a failed copy can never masquerade as a complete destination.
                entry.Staging = TrashPath(policy.Root, row.Path + ".staging");
                ReportStore.WriteJson(journalPath, journal);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Staging)!);
                await Copy(row.Path, entry.Staging, row.IsDirectory, token);
                if (await HashService.Fingerprint(entry.Staging, row.IsDirectory, token) != entry.Hash || await HashService.Fingerprint(row.Path, row.IsDirectory, token) != entry.Hash)
                    throw new IOException("Copy verification failed; source retained");
                Move(entry.Staging, destination, row.IsDirectory);
                if (useRecycle)
                {
                    entry.Action = "Recycle";
                    ReportStore.WriteJson(journalPath, journal);
                    Recycle(row.Path);
                }
            }
            else Move(row.Path, destination, row.IsDirectory);
            SetTimes(destination, entry);
            entry.State = "Applied";
            ReportStore.WriteJson(journalPath, journal);
        }
        if (organize) Summary(rows);
    }

    public async Task Undo(string journalPath, bool execute, bool reviewed, CancellationToken token)
    {
        Console.WriteLine(Banner);
        if (execute && !reviewed) throw new ArgumentException("Undo execution requires --reviewed and --no-dry-run");
        var journal = ReportStore.ReadJson<UndoJournal>(journalPath);
        if (journal.Version != 1) throw new InvalidDataException("Unsupported undo version");
        var policy = new PathPolicy(new(journal.Root, journal.Excludes));
        foreach (var entry in journal.Entries.AsEnumerable().Reverse().Where(e => e.State != "Undone"))
        {
            token.ThrowIfCancellationRequested();
            policy.AssertAllowed(entry.Original);
            policy.AssertAllowed(entry.Destination, allowInternal: entry.Action is "Trash" or "Recycle");
            if (entry.Action is not ("Move" or "Copy" or "Trash" or "Recycle")) throw new InvalidDataException("Invalid journal action");
            bool originalExists = Exists(entry.Original), destinationExists = Exists(entry.Destination);
            if (originalExists && await HashService.Fingerprint(entry.Original, entry.IsDirectory, token) != entry.Hash) throw new IOException($"Undo would overwrite a changed file: {entry.Original}");
            if (destinationExists && await HashService.Fingerprint(entry.Destination, entry.IsDirectory, token) != entry.Hash) throw new IOException($"Undo destination changed: {entry.Destination}");
            if (!originalExists && !destinationExists) throw new IOException($"Neither original nor destination exists: {entry.Original}");
            if (entry.Action == "Copy" && !originalExists) throw new IOException("Copy original is missing; restore it manually before undo");
            Console.WriteLine($"UNDO {entry.Action}: {entry.Destination} -> {entry.Original}");
            if (!execute) continue;
            if (entry.Action == "Copy" && destinationExists)
            {
                entry.UndoDestination ??= TrashPath(policy.Root, entry.Destination);
                policy.AssertAllowed(entry.UndoDestination, true);
                ReportStore.WriteJson(journalPath, journal);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.UndoDestination)!);
                Move(entry.Destination, entry.UndoDestination, entry.IsDirectory);
            }
            else if (!originalExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Original)!);
                Move(entry.Destination, entry.Original, entry.IsDirectory);
                SetTimes(entry.Original, entry);
            }
            else if (destinationExists) Log.Warning("Original already present; retaining verified backup at {Path}", entry.Destination);
            if (entry.Staging != null && Exists(entry.Staging)) Log.Warning("Interrupted copy staging retained for inspection: {Path}", entry.Staging);
            entry.State = "Undone";
            ReportStore.WriteJson(journalPath, journal);
        }
    }

    public static string Collision(string target, string hash, HashSet<string>? reserved = null)
    {
        reserved ??= new(PathPolicy.Comparer);
        var candidate = target;
        int count = 0;
        while (Exists(candidate) || reserved.Contains(candidate)) candidate = Suffix(target, "_" + hash[..8] + (count++ == 0 ? "" : "_" + count));
        reserved.Add(candidate);
        return candidate;
    }

    private static string Suffix(string path, string suffix) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static string TrashPath(string root, string source) => Path.Combine(root, "_Trash", Guid.NewGuid().ToString("N"), Path.GetFileName(source));
    private static void Move(string source, string destination, bool directory) { if (directory) Directory.Move(source, destination); else File.Move(source, destination, false); }
    private static List<FileRecord> Companions(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!MediaService.Images.Contains(ext) && ext != ".mov") return [];
        var file = new FileRecord { Path = path, Extension = ext };
        var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Select(p => new FileRecord { Path = p, Extension = Path.GetExtension(p).ToLowerInvariant() }).ToList();
        return DedupService.Pair(siblings, file);
    }

    private static async Task Copy(string source, string target, bool directory, CancellationToken token)
    {
        if (directory)
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                PathPolicy.AssertNoLinks(child);
                await Copy(child, Path.Combine(target, Path.GetFileName(child)), Directory.Exists(child), token);
            }
            Directory.SetCreationTimeUtc(target, Directory.GetCreationTimeUtc(source));
            Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(source));
        }
        else
        {
            await using (var input = HashService.Open(source))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
    }

    private static void SetTimes(string path, UndoEntry entry)
    {
        if (entry.IsDirectory) { Directory.SetCreationTimeUtc(path, entry.CreatedUtc); Directory.SetLastWriteTimeUtc(path, entry.LastWriteUtc); }
        else { File.SetCreationTimeUtc(path, entry.CreatedUtc); File.SetLastWriteTimeUtc(path, entry.LastWriteUtc); }
    }

    private static void Summary(List<ReportRow> rows)
    {
        Console.WriteLine("Resulting category tree (plan, including retained/review entries):");
        foreach (var group in rows.GroupBy(r => r.Status == "Review" ? "_Review (not moved)" : r.Category).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {group.Count()} items, {group.Sum(r => r.Size):N0} bytes");
    }

    private static void Recycle(string path)
    {
        // Never let the Shell implicitly delete connected HTML resource folders or recurse.
        var operation = new ShellFileOperation { Function = 3, From = path + '\0' + '\0', Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 | 0x1000 | 0x2000 };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.Aborted || File.Exists(path)) throw new IOException($"Recycle Bin operation failed ({result}); undo backup retained");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref ShellFileOperation operation);
}
