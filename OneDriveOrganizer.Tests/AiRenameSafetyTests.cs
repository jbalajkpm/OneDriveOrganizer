using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;

namespace OneDriveOrganizer.Tests;

public sealed class AiRenameSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-rename-tests-" + Guid.NewGuid().ToString("N"));
    public AiRenameSafetyTests() => Directory.CreateDirectory(root);
    private ReportRow FileRow(string name, string suggested, string status = "Move")
    {
        string path = Path.Combine(root, name); File.WriteAllText(path, name);
        var info = new FileInfo(path);
        return new ReportRow { Path = path, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Status = status, SuggestedFileName = suggested, TargetPath = Path.Combine(root, "Photos", name) };
    }
    private async Task<string> Plan(params ReportRow[] rows)
    {
        string output = Path.Combine(root, "plan.csv");
        await new ReportStore().Write("classify", new(root, []), output, rows.ToList(), CancellationToken.None);
        return output;
    }
    private static Task Rename(string plan) => new ExecutionService().Apply(plan, true, true, true, false, false, null, CancellationToken.None, rename: true);

    [Fact]
    public async Task RenameCannotSplitLivePhotoStems()
    {
        var photo = FileRow("pair.jpg", "renamed-photo"); var video = FileRow("pair.mov", "renamed-video");
        string plan = await Plan(photo, video);
        await Assert.ThrowsAsync<IOException>(() => Rename(plan));
        Assert.True(File.Exists(photo.Path)); Assert.True(File.Exists(video.Path));
    }

    [Fact]
    public async Task RemovingCompanionRowCannotAuthorizePartialRename()
    {
        var photo = FileRow("pair.jpg", "renamed"); var video = FileRow("pair.mov", "renamed");
        string plan = await Plan(photo, video); ReportStore.WriteCsv(plan, new[] { photo });
        await Assert.ThrowsAsync<IOException>(() => Rename(plan));
    }

    [Fact]
    public async Task ReviewRowsAreNeverRenamed()
    {
        var row = FileRow("review.txt", "descriptive", "Review");
        await Rename(await Plan(row)); Assert.True(File.Exists(row.Path));
        Assert.False(File.Exists(Path.Combine(root, "descriptive.txt")));
    }

    [Fact]
    public async Task SuggestedPathTraversalIsRejectedBeforeMutation()
    {
        var row = FileRow("source.txt", "..\\escape");
        string plan = await Plan(row);
        await Assert.ThrowsAsync<InvalidDataException>(() => Rename(plan));
        Assert.True(File.Exists(row.Path));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
