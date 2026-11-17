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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
    }
}
