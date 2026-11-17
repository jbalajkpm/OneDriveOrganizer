using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;
using ImageMagick;

namespace OneDriveOrganizer.Tests;

public sealed class OrganizerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MediaService media = new();
    private readonly ReportStore reports = new();
    private ScanOptions Options => new(root, [], true, 2);
    private IndexService Index => new(media);
    private DedupService Dedup => new(Index, reports, media);
    private ClassificationService Classifier => new(Index, new RuleEngine(), reports, media);
    private static CancellationToken Token => CancellationToken.None;

    public OrganizerTests() => Directory.CreateDirectory(root);
    private string P(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    private string Put(string path, string content)
    {
        var result = P(path); Directory.CreateDirectory(Path.GetDirectoryName(result)!); File.WriteAllText(result, content); return result;
    }

    [Fact]
    public async Task HashStagesDistinguishEqualPrefixes()
    {
        var a = Put("a.bin", new string('x', HashService.PartialBytes) + "A");
        var b = Put("b.bin", new string('x', HashService.PartialBytes) + "B");
        Assert.Equal(await HashService.Sha256(a, Token, true), await HashService.Sha256(b, Token, true));
        Assert.NotEqual(await HashService.Sha256(a, Token), await HashService.Sha256(b, Token));
        Assert.Empty(await Dedup.Dupes(Options, P("dupes.csv"), "oldest", null, Token));
    }

    [Fact]
    public async Task CrcMatchesStandardVector()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926U, await HashService.Crc32(stream, Token));
    }

    [Fact]
    public void DHashAndHammingHaveKnownResults()
    {
        using var image = new MagickImage(MagickColors.Black, 9, 8);
        using (var pixels = image.GetPixels())
            for (int y = 0; y < 8; y++) for (int x = 0; x < 9; x++)
            {
                byte value = (byte)(255 - x * 25);
                pixels.SetPixel(x, y, [value, value, value]);
            }
        Assert.Equal(ulong.MaxValue, MediaService.DHash(image));
        Assert.Equal(64, HashService.Hamming(ulong.MaxValue, 0));
        Assert.Equal(6, HashService.Hamming(63, 0));
    }

    [Theory]
    [InlineData("IMG_20240817_123000.jpg", 2024, 8, 17)]
    [InlineData("WhatsApp Image 2024-08-17 at 12.30.jpg", 2024, 8, 17)]
    [InlineData("Screenshot 2024-08-17.png", 2024, 8, 17)]
    public void FilenameDates(string name, int year, int month, int day) => Assert.Equal(new DateTime(year, month, day), MediaService.FilenameDate(name));

    [Theory]
    [InlineData("IMG_20240230.jpg")]
    [InlineData("random123456789.jpg")]
    public void InvalidDatesDoNotInventDates(string name) => Assert.Null(MediaService.FilenameDate(name));

    [Theory]
    [InlineData("Report-DESKTOP-ABC123.docx", "DeviceConflict")]
    [InlineData("Photo (1).jpg", "PossibleConflict")]
    [InlineData("file - Copy.jpg", "PossibleConflict")]
    [InlineData("normal.jpg", "")]
    public void ConflictsAreCandidatesNotProof(string name, string expected) => Assert.Equal(expected, PathPolicy.Conflict(name));

    [Fact]
    public void BothOfflineFlagsAreRecognized()
    {
        Assert.True(PathPolicy.IsOffline(FileAttributes.Offline));
        Assert.True(PathPolicy.IsOffline((FileAttributes)0x400000));
        Assert.False(PathPolicy.IsOffline(FileAttributes.Archive));
    }

    [Fact]
    public void RuleMatcherCombinesConstraints()
    {
        var rule = new Rule { Extensions = [".pdf"], MagicBytes = ["25504446"], FilenameRegex = "invoice", TextKeywords = ["tax"], MinSize = 10, MaxSize = 100, ExifPresence = false };
        var file = new FileRecord { Path = "invoice.pdf", Extension = ".pdf", Size = 30 };
        Assert.True(RuleEngine.Matches(rule, file, Encoding.ASCII.GetBytes("%PDF"), "tax"));
        Assert.False(RuleEngine.Matches(rule, file, Encoding.ASCII.GetBytes("oops"), "tax"));
        Assert.False(RuleEngine.Matches(rule, file, Encoding.ASCII.GetBytes("%PDF"), "hello"));
        Assert.NotEmpty(new RuleEngine().Load(null));
    }

    [Fact]
    public void CsvRoundTripsUnicodeQuotesNewlinesAndDates()
    {
        var row = new ReportRow { Path = "photo,\"cafe\u0301\"\n\u65e5\u672c.jpg", Reason = "line 1\r\nline 2", LastWriteUtc = DateTime.UtcNow, Keep = false, Confidence = 0.9123456789, Sensitive = true };
        ReportStore.WriteCsv(P("roundtrip.csv"), [row]);
        Assert.Equal(row, Assert.Single(ReportStore.ReadCsv(P("roundtrip.csv"))));
    }

    [Fact]
    public void CollisionNeverOverwritesOrReusesReservedTargets()
    {
        var target = Put("photo.jpg", "original");
        var reserved = new HashSet<string>(PathPolicy.Comparer);
        var first = ExecutionService.Collision(target, "ABCDEF0123456789", reserved);
        var second = ExecutionService.Collision(target, "ABCDEF0123456789", reserved);
        Assert.Contains("ABCDEF01", first); Assert.NotEqual(first, second); Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void RootContainmentUsesDirectoryBoundaries()
    {
        Assert.True(PathPolicy.Within(P("one/two"), root));
        Assert.False(PathPolicy.Within(root + "-sibling", root));
        Assert.Throws<IOException>(() => new PathPolicy(Options).AssertAllowed(P("../escape")));
    }

    [Fact]
    public async Task IndexIsIncrementalAndNeverIncludesProtectedFiles()
    {
        var path = Put("a.txt", "content");
        Put("excluded/b.txt", "secret"); Put("desktop.ini", "system"); Put("shortcut.lnk", "link");
        var options = Options with { Excludes = ["excluded"] };
        var first = await Index.Refresh(options, Token);
        Assert.Single(first); Assert.Null(first[0].FullHash);
        await Index.FullHashes(first, options, Token); IndexService.Persist(root, first);
        var second = await Index.Refresh(options, Token);
        Assert.Equal(first[0].FullHash, second[0].FullHash);
        File.AppendAllText(path, "changed");
        Assert.Null(Assert.Single(await Index.Refresh(options, Token)).FullHash);
    }

    [Fact]
    public async Task IntegrationReportsKnownDuplicatesZipAndImages()
    {
        Put("a.txt", "known duplicate"); Put("b.txt", "known duplicate");
        using (var zip = ZipFile.Open(P("bundle.zip"), ZipArchiveMode.Create)) zip.CreateEntryFromFile(P("a.txt"), "a.txt");
        using (var image = new MagickImage(MagickColors.Black, 80, 60)) { image.Write(P("first.png"), MagickFormat.Png); image.Write(P("second.png"), MagickFormat.Png); }
        var dupes = await Dedup.Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        Assert.Equal(2, dupes.Count(r => !r.Keep));
        var zipRows = await new ArchiveService(Index, reports).Analyze(Options, P("zips.csv"), Token);
        Assert.Equal("Redundant", Assert.Single(zipRows).Status); Assert.False(zipRows[0].Keep);
        var photoRows = await Dedup.Photos(Options, P("photos.csv"), 6, false, null, null, Token);
        Assert.Single(photoRows, r => !r.Keep);
        Assert.All(photoRows, r => Assert.StartsWith("P", r.GroupId));
        Assert.Equal(dupes, ReportStore.ReadReviewed(P("dupes.csv")).Rows);
    }

    [Fact]
    public async Task CleanRequiresBothAuthorizationsAndUndoRestores()
    {
        Put("a.txt", "duplicate"); Put("b.txt", "duplicate");
        var rows = await Dedup.Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        var removed = Assert.Single(rows, r => !r.Keep);
        var execution = new ExecutionService();
        await execution.Apply(P("dupes.csv"), false, false, false, false, false, null, Token);
        Assert.True(File.Exists(removed.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => execution.Apply(P("dupes.csv"), false, true, false, false, false, P("undo.json"), Token));
        await execution.Apply(P("dupes.csv"), false, true, true, false, false, P("undo.json"), Token);
        Assert.False(File.Exists(removed.Path)); Assert.True(File.Exists(removed.KeptPath));
        await execution.Undo(P("undo.json"), false, false, Token); Assert.False(File.Exists(removed.Path));
        await execution.Undo(P("undo.json"), true, true, Token); Assert.Equal("duplicate", File.ReadAllText(removed.Path));
        await execution.Undo(P("undo.json"), true, true, Token);
    }

    [Fact]
    public async Task StaleReportFailsBeforeAnyDeletionEvenWithSameSizeAndTimestamp()
    {
        Put("a.txt", "duplicate"); Put("b.txt", "duplicate");
        var rows = await Dedup.Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        var row = rows[0]; File.WriteAllText(row.Path, "different"); File.SetLastWriteTimeUtc(row.Path, row.LastWriteUtc);
        await Assert.ThrowsAsync<IOException>(() => new ExecutionService().Apply(P("dupes.csv"), false, true, true, false, false, null, Token));
        Assert.All(rows, r => Assert.True(File.Exists(r.Path)));
    }

    [Fact]
    public async Task EditedReportsMayOnlyRemoveRowsOrProtectMoreFiles()
    {
        Put("a.txt", "same"); Put("b.txt", "same");
        var rows = await Dedup.Dupes(Options, P("dupes.csv"), "oldest", null, Token);
        rows[0].Path = P("unrelated.txt"); ReportStore.WriteCsv(P("dupes.csv"), rows);
        Assert.Throws<InvalidDataException>(() => ReportStore.ReadReviewed(P("dupes.csv")));
    }

    [Fact]
    public async Task OrganizeCollisionPreservesTimestampsAndUndo()
    {
        var original = Put("incoming/note.txt", "ordinary text");
        Put("Documents/Text/note.txt", "already here");
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc); File.SetLastWriteTimeUtc(original, time);
        await Classifier.Classify(Options, P("plan.csv"), null, 0.7, false, 3, Token);
        var execution = new ExecutionService();
        await execution.Apply(P("plan.csv"), true, true, true, false, false, P("undo.json"), Token);
        Assert.False(File.Exists(original)); Assert.Equal("already here", File.ReadAllText(P("Documents/Text/note.txt")));
        var entry = Assert.Single(ReportStore.ReadJson<UndoJournal>(P("undo.json")).Entries);
        Assert.Equal(time, File.GetLastWriteTimeUtc(entry.Destination)); Assert.Contains('_', Path.GetFileName(entry.Destination));
        await execution.Undo(P("undo.json"), true, true, Token); Assert.Equal("ordinary text", File.ReadAllText(original));
    }

    [Fact]
    public async Task CopyUndoRetainsOriginalAndQuarantinesCopy()
    {
        var original = Put("incoming/note.txt", "ordinary text");
        await Classifier.Classify(Options, P("plan.csv"), null, 0.7, false, 3, Token);
        var execution = new ExecutionService();
        await execution.Apply(P("plan.csv"), true, true, true, false, true, P("undo.json"), Token);
        var entry = Assert.Single(ReportStore.ReadJson<UndoJournal>(P("undo.json")).Entries);
        Assert.True(File.Exists(original)); Assert.True(File.Exists(entry.Destination));
        await execution.Undo(P("undo.json"), true, true, Token);
        Assert.True(File.Exists(original)); Assert.False(File.Exists(entry.Destination));
    }

    [Fact]
    public async Task RawJpegAndLivePhotoPairsAreNotSplit()
    {
        Put("incoming/IMG_20240817.heic", "unsupported HEIC"); Put("incoming/IMG_20240817.mov", "unsupported MOV");
        var rows = await Classifier.Classify(Options, P("plan.csv"), null, 0.7, false, 3, Token);
        Assert.Equal(Path.GetDirectoryName(rows[0].TargetPath), Path.GetDirectoryName(rows[1].TargetPath));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task RepositoryIsOneUnitAndProtectedDescendantsPreventMoves()
    {
        Put("repo/.git/config", "git config"); Put("repo/main.cs", "class Example {}"); Put("repo/desktop.ini", "protected");
        var rows = await Classifier.Classify(Options, P("plan.csv"), null, 0.7, false, 3, Token);
        var row = Assert.Single(rows); Assert.True(row.IsDirectory); Assert.Equal("Protected", row.Status);
    }

    [Fact]
    public async Task LowConfidenceItemsArePlannedForReviewButNotMoved()
    {
        await File.WriteAllBytesAsync(P("unknown.blob"), [0, 1, 2, 0]);
        var rows = await Classifier.Classify(Options, P("plan.csv"), null, 0.7, false, 3, Token);
        var row = Assert.Single(rows); Assert.Equal("Review", row.Status); Assert.Contains("_Review", row.TargetPath);
        await new ExecutionService().Apply(P("plan.csv"), true, true, true, false, false, null, Token);
        Assert.True(File.Exists(P("unknown.blob")));
    }

    [Fact]
    public void MagicOverridesMisleadingExtension()
    {
        Assert.Equal("PDF", ClassificationService.DetectType(Encoding.ASCII.GetBytes("%PDF-1.7"), "photo.jpg"));
        Assert.Equal("Image", ClassificationService.DetectType(Convert.FromHexString("89504E470D0A1A0A"), "image.txt"));
    }

    [Fact]
    public async Task HtmlEncodesFilenamesAndContainsNoRemoteAssets()
    {
        Put("a & b.txt", "content");
        await new HtmlReportService(Index).Write(Options, P("summary.html"), Token);
        var html = await File.ReadAllTextAsync(P("summary.html"));
        Assert.Contains("a &amp; b.txt", html); Assert.Contains("<svg", html); Assert.DoesNotContain("<script", html);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
