using System.IO.Compression;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;

namespace OneDriveOrganizer.Tests;

public sealed class SafetyRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-safety-" + Guid.NewGuid().ToString("N"));
    private readonly MediaService media = new();
    private readonly ReportStore reports = new();
    private ScanOptions Options => new(root, [], true, 2);
    private IndexService Index => new(media);
    private static CancellationToken Token => CancellationToken.None;
    private string P(string name) => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
    public SafetyRegressionTests() => Directory.CreateDirectory(root);
    private string Put(string name, string content)
    {
        var path = P(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }
    private Task<List<ReportRow>> Plan(string name = "plan.csv") => new ClassificationService(Index, new RuleEngine(), reports, media).Classify(Options, P(name), null, 0.7, false, 3, Token);

    [Fact]
    public async Task RemovingOneCompanionFromPlanCannotSplitThePair()
    {
        Put("incoming/pair.heic", "HEIC metadata unavailable"); Put("incoming/pair.mov", "MOV metadata unavailable");
        var rows = await Plan();
        ReportStore.WriteCsv(P("plan.csv"), rows.Take(1));
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(P("plan.csv"), true, true, true, false, false, null, Token));
        Assert.True(File.Exists(P("incoming/pair.heic"))); Assert.True(File.Exists(P("incoming/pair.mov")));
    }

    [Fact]
    public async Task CompanionCollisionsKeepMatchingStemsAcrossDifferentSourceFolders()
    {
        Put("first/pair.heic", "HEIC first"); Put("first/pair.mov", "MOV first");
        Put("second/pair.heic", "HEIC second"); Put("second/pair.mov", "MOV second");
        await Plan();
        await new ExecutionService().Apply(P("plan.csv"), true, true, true, false, false, P("undo.json"), Token);
        var entries = ReportStore.ReadJson<UndoJournal>(P("undo.json")).Entries;
        Assert.Equal(4, entries.Count);
        foreach (var group in entries.GroupBy(e => Path.GetDirectoryName(e.Original)))
            Assert.Single(group.Select(e => Path.GetFileNameWithoutExtension(e.Destination)).Distinct());
        Assert.Equal(4, entries.Select(e => e.Destination).Distinct().Count());
    }

    [Fact]
    public async Task NewlyAddedCompanionBlocksAnOldDeletionReport()
    {
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Black, 8, 8))
        { image.Write(P("one.jpg"), ImageMagick.MagickFormat.Jpeg); image.Write(P("two.jpg"), ImageMagick.MagickFormat.Jpeg); }
        var rows = await new DedupService(Index, reports, media).Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        var deletion = Assert.Single(rows, r => !r.Keep);
        File.WriteAllText(Path.ChangeExtension(deletion.Path, ".mov"), "new companion");
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(P("dupes.csv"), false, true, true, false, false, null, Token));
        Assert.True(File.Exists(deletion.Path));
    }

    [Fact]
    public async Task ChangedOrganizedFileBlocksUndoWithoutOverwriting()
    {
        var original = Put("incoming/note.txt", "before");
        await Plan();
        var service = new ExecutionService();
        await service.Apply(P("plan.csv"), true, true, true, false, false, P("undo.json"), Token);
        var entry = Assert.Single(ReportStore.ReadJson<UndoJournal>(P("undo.json")).Entries);
        File.WriteAllText(entry.Destination, "edited later");
        await Assert.ThrowsAsync<IOException>(() => service.Undo(P("undo.json"), true, true, Token));
        Assert.Equal("edited later", File.ReadAllText(entry.Destination)); Assert.False(File.Exists(original));
    }

    [Fact]
    public async Task PreparedMoveJournalRecoversAfterInterruptedFinalSave()
    {
        var original = Put("incoming/note.txt", "before");
        await Plan();
        var service = new ExecutionService();
        await service.Apply(P("plan.csv"), true, true, true, false, false, P("undo.json"), Token);
        var journal = ReportStore.ReadJson<UndoJournal>(P("undo.json")); journal.Entries[0].State = "Prepared";
        ReportStore.WriteJson(P("undo.json"), journal);
        await service.Undo(P("undo.json"), true, true, Token);
        Assert.Equal("before", File.ReadAllText(original));
    }

    [Fact]
    public async Task ArchiveStatesIncludePartialEmptyNestedAndUnsupported()
    {
        Put("known.txt", "known");
        using (var zip = ZipFile.Open(P("partial.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(P("known.txt"), "known.txt");
            using var writer = new StreamWriter(zip.CreateEntry("nested.zip").Open()); writer.Write("unique nested bytes");
        }
        using (ZipFile.Open(P("empty.zip"), ZipArchiveMode.Create)) { }
        Put("unknown.7z", "unsupported");
        var rows = await new ArchiveService(Index, reports).Analyze(Options, P("zips.csv"), Token);
        Assert.Equal("Partial", rows.Single(r => r.Path.EndsWith("partial.zip")).Status);
        Assert.Contains("nested ZIP: True", rows.Single(r => r.Path.EndsWith("partial.zip")).Reason);
        Assert.Equal("Unique", rows.Single(r => r.Path.EndsWith("empty.zip")).Status);
        Assert.Equal("Unsupported", rows.Single(r => r.Path.EndsWith("unknown.7z")).Status);
        Assert.All(rows, r => Assert.True(r.Keep));
    }

    [Fact]
    public async Task LooseZipKeeperChangesBlockCleanup()
    {
        var loose = Put("known.txt", "known");
        using (var zip = ZipFile.Open(P("bundle.zip"), ZipArchiveMode.Create)) zip.CreateEntryFromFile(loose, "known.txt");
        await new ArchiveService(Index, reports).Analyze(Options, P("zips.csv"), Token);
        File.WriteAllText(loose, "changed");
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(P("zips.csv"), false, true, true, false, false, null, Token));
        Assert.True(File.Exists(P("bundle.zip")));
    }

    [Fact]
    public async Task FolderMultisetsDetectEqualityAndStrictSubset()
    {
        Put("left/a.txt", "one"); Put("right/renamed.txt", "one"); Put("larger/a.txt", "one"); Put("larger/b.txt", "two");
        var rows = await new DedupService(Index, reports, media).Folders(Options, P("folders.csv"), Token);
        Assert.Contains(rows, r => r.Status == "DuplicateTree"); Assert.Contains(rows, r => r.Status == "StrictSubset");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExecutionService().Apply(P("folders.csv"), false, true, true, false, false, null, Token));
    }

    [Fact]
    public void InternalPathsStillRespectExplicitExcludes()
    {
        var policy = new PathPolicy(Options with { Excludes = ["_Trash"] });
        Assert.Throws<IOException>(() => policy.AssertAllowed(P("_Trash/one/file.txt"), true));
        Assert.True(PathPolicy.Within(root, Path.GetPathRoot(root)!));
    }

    [Fact]
    public async Task CancellationLeavesSourcesAndNoExecutableReport()
    {
        Put("a.txt", "same"); Put("b.txt", "same");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DedupService(Index, reports, media).Dupes(Options, P("dupes.csv"), "oldest", null, cts.Token));
        Assert.True(File.Exists(P("a.txt"))); Assert.False(File.Exists(P("dupes.csv.manifest.json")));
    }

    [Fact]
    public async Task RulesCannotEscapeTheRoot()
    {
        Put("invoice.txt", "invoice");
        ReportStore.WriteJson(P("custom.json"), new[] { new Rule { Name = "Escape", Extensions = [".txt"], Target = "../outside", Priority = 1000 } });
        await Assert.ThrowsAsync<IOException>(() => new ClassificationService(Index, new RuleEngine(), reports, media).Classify(Options, P("plan.csv"), P("custom.json"), 0.7, false, 3, Token));
    }

    [Fact]
    public async Task OneDriveSyncMarkerIsProtectedAndRemovedFromOldIndexes()
    {
        var marker = Put(".849C9593-D756-4E56-8D6E-42412F2A707B", "sync state");
        var ordinary = Put(".12345678-1234-1234-1234-123456789012", "user content");
        using (var store = new IndexStore(root)) store.Save([new FileRecord { Path = marker }]);
        var files = await Index.Refresh(Options, Token);
        Assert.Equal(ordinary, Assert.Single(files).Path);
        using (var store = new IndexStore(root)) Assert.False(store.Load().ContainsKey(marker));
        Assert.True(PathPolicy.ProtectedName(marker.ToLowerInvariant()));
        Assert.Throws<IOException>(() => new PathPolicy(Options).AssertAllowed(marker));
        Assert.Equal("sync state", File.ReadAllText(marker));
    }

    [WindowsFileLockFact]
    public async Task LockedFilesAreSkippedAndRetriedAfterUnlocking()
    {
        var readable = Put("readable.txt", "available");
        var locked = Put("locked.txt", "busy");
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(readable, Assert.Single(await Index.Refresh(Options, Token)).Path);
            using (var store = new IndexStore(root)) Assert.Equal(readable, Assert.Single(store.Load()).Key);
            await Assert.ThrowsAsync<IOException>(() => HashService.Sha256(locked, Token));
        }
        var refreshed = await Index.Refresh(Options, Token);
        Assert.Equal(2, refreshed.Count);
        Assert.Equal(await HashService.Sha256(locked, Token, partial: true), refreshed.Single(f => f.Path == locked).PartialHash);
        Assert.Equal("busy", File.ReadAllText(locked));
    }

    [WindowsFileLockFact]
    public async Task CachedFilesAreNotReusedWhileLocked()
    {
        var locked = Put("cached.txt", "unchanged");
        var files = await Index.Refresh(Options, Token);
        await Index.FullHashes(files, Options, Token);
        IndexService.Persist(root, files);
        Assert.NotNull(Assert.Single(files).FullHash);
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(files[0].LastWriteUtc, File.GetLastWriteTimeUtc(locked));
            Assert.Empty(await Index.Refresh(Options, Token));
            using var store = new IndexStore(root);
            Assert.Empty(store.Load());
        }
        var refreshed = Assert.Single(await Index.Refresh(Options, Token));
        Assert.Equal(locked, refreshed.Path);
        Assert.Equal(files[0].PartialHash, refreshed.PartialHash);
        Assert.Null(refreshed.FullHash);
    }

    [WindowsFileLockFact]
    public async Task LockAfterReportCreationStillBlocksCleanup()
    {
        Put("a.txt", "duplicate"); Put("b.txt", "duplicate");
        var rows = await new DedupService(Index, reports, media).Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        var deletion = Assert.Single(rows, r => !r.Keep);
        using var held = new FileStream(deletion.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(P("dupes.csv"), false, true, true, false, false, P("undo.json"), Token));
        Assert.All(rows, row => Assert.True(File.Exists(row.Path)));
        Assert.False(File.Exists(P("undo.json")));
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task CachedUnicodeDocumentsArePlannedAsTextAndMovedWithoutChangingEncoding(int codePage)
    {
        var path = P("diagnostic.txt");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("Diagnostic report\r\nName\tValue\r\n", 500)), System.Text.Encoding.GetEncoding(codePage));
        var bytes = File.ReadAllBytes(path);
        await Index.Refresh(Options, Token);
        var row = Assert.Single(await Plan());
        Assert.Equal("Text", row.DetectedType);
        Assert.Equal("Documents/Text", row.Category);
        Assert.Equal("Move", row.Status);
        Assert.Equal(P("Documents/Text/diagnostic.txt"), row.TargetPath);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var execution = new ExecutionService();
        await execution.Apply(P("plan.csv"), true, false, false, false, false, P("undo.json"), Token);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(P("undo.json")));
        await execution.Apply(P("plan.csv"), true, true, true, false, false, P("undo.json"), Token);
        Assert.False(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(row.TargetPath));
        await execution.Undo(P("undo.json"), true, true, Token);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(".loop")]
    [InlineData(".LOOP")]
    [InlineData(".LoOp")]
    public async Task LoopFilesAreExcludedFromIndexesAndRepositoryMoves(string extension)
    {
        var loop = Put("repository/meeting" + extension, "Loop content must not be read");
        var ordinary = Put("ordinary.txt", "available");
        using (var store = new IndexStore(root)) store.Save([new FileRecord { Path = loop }]);
        using var held = new FileStream(loop, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(ordinary, Assert.Single(await Index.Refresh(Options with { SkipOffline = false }, Token)).Path);
        using (var store = new IndexStore(root)) Assert.False(store.Load().ContainsKey(loop));
        Assert.Throws<IOException>(() => new PathPolicy(Options).AssertAllowed(loop));
        var error = await Assert.ThrowsAsync<IOException>(() => HashService.Fingerprint(P("repository"), true, Token));
        Assert.Equal($"Repository contains a protected/offline/link entry: {loop}", error.Message);
        Directory.CreateDirectory(P("repository/.git"));
        Put("repository/code.cs", "class Example { }");
        var repository = Assert.Single(await Plan(), row => row.IsDirectory);
        Assert.Equal("Protected", repository.Status);
    }

    [Theory]
    [InlineData("classify", true)]
    [InlineData("dupes", false)]
    public async Task OldReportsCannotMoveOrDeleteLoopFiles(string kind, bool organize)
    {
        var loop = Put("meeting.loop", "preserve me");
        var keeper = Put("keeper.txt", "preserve me");
        var row = new ReportRow { Path = loop, TargetPath = P("moved.loop"), KeptPath = keeper, Keep = false, Status = organize ? "Move" : "ExactDuplicate" };
        var report = P("old.csv");
        ReportStore.WriteCsv(report, [row]);
        ReportStore.WriteJson(report + ".manifest.json", new ReportManifest { Root = root, Kind = kind, Rows = [row], MediaAnalysisVersion = MediaService.AnalysisVersion });
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(report, organize, true, true, false, false, P("undo.json"), Token));
        Assert.Equal("preserve me", File.ReadAllText(loop));
        Assert.False(File.Exists(P("undo.json")));
    }

    [Fact]
    public async Task OldUndoJournalsCannotRestoreLoopFiles()
    {
        var original = P("meeting.loop");
        var destination = Put("_Trash/meeting.loop", "preserve me");
        var journal = P("old-undo.json");
        ReportStore.WriteJson(journal, new UndoJournal { Root = root, Entries =
            [new UndoEntry { Original = original, Destination = destination, Action = "Trash", State = "Applied" }] });
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Undo(journal, true, true, Token));
        Assert.False(File.Exists(original));
        Assert.Equal("preserve me", File.ReadAllText(destination));
    }

    private sealed class WindowsFileLockFactAttribute : FactAttribute
    {
        public WindowsFileLockFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows mandatory file-sharing locks.";
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
    }
}
