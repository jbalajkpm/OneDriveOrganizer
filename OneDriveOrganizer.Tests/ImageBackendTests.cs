using ImageMagick;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;

namespace OneDriveOrganizer.Tests;

public sealed class ImageBackendTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-images-" + Guid.NewGuid().ToString("N"));
    private readonly MediaService media = new();
    private ScanOptions Options => new(root, [], true, 2);
    private static CancellationToken Token => CancellationToken.None;
    private string P(string name) => Path.Combine(root, name);
    public ImageBackendTests() => Directory.CreateDirectory(root);

    private static MagickImage Pattern(uint width = 80, uint height = 40)
    {
        var image = new MagickImage(MagickColors.Black, width, height);
        using var pixels = image.GetPixels();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels.SetPixel(x, y, [(byte)(255 - x * 2), (byte)(y * 5), (byte)((x + y) * 2)]);
        return image;
    }

    private FileRecord Record(string path)
    {
        var info = new FileInfo(path);
        return new FileRecord { Path = path, Extension = info.Extension.ToLowerInvariant(), Size = info.Length, CreatedUtc = info.CreationTimeUtc, LastWriteUtc = info.LastWriteTimeUtc };
    }

    [Theory]
    [InlineData(".jpg", MagickFormat.Jpeg)]
    [InlineData(".jpeg", MagickFormat.Jpeg)]
    [InlineData(".png", MagickFormat.Png)]
    [InlineData(".webp", MagickFormat.WebP)]
    [InlineData(".gif", MagickFormat.Gif)]
    [InlineData(".bmp", MagickFormat.Bmp)]
    [InlineData(".tif", MagickFormat.Tiff)]
    [InlineData(".tiff", MagickFormat.Tiff)]
    public async Task AllPreviouslyDecodedFormatsRetainDimensionsHashesAndColorAnalysis(string extension, MagickFormat format)
    {
        var path = P("image" + extension);
        using (var image = Pattern()) image.Write(path, format);
        var before = await HashService.Sha256(path, Token);
        var file = Record(path);
        await media.Read(file, Token);
        Assert.Equal("Decoded", file.MediaStatus);
        Assert.Equal(80, file.Width); Assert.Equal(40, file.Height);
        Assert.NotNull(file.PerceptualHash);
        Assert.True(file.ColorVariance > 0);
        Assert.Equal(MediaService.AnalysisVersion, file.MediaAnalysisVersion);
        Assert.Equal(before, await HashService.Sha256(path, Token));
    }

    [Theory]
    [InlineData(1, 80, 40)]
    [InlineData(3, 80, 40)]
    [InlineData(6, 40, 80)]
    [InlineData(8, 40, 80)]
    public async Task ExifOrientationDateAndCameraArePreserved(int orientation, int width, int height)
    {
        var path = P("oriented.tiff");
        using var image = Pattern();
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:08:17 12:34:56");
        exif.SetValue(ExifTag.Make, "TestCamera");
        exif.SetValue(ExifTag.Model, "ModelOne");
        image.SetProfile(exif);
        image.Orientation = (Orientation)orientation;
        image.Write(path, MagickFormat.Tiff);
        // The TIFF writer does not serialize the EXIF SubIFD; use JPEG for those metadata fields.
        image.Write(P("metadata.jpg"), MagickFormat.Jpeg);
        image.AutoOrient();
        var expectedHash = MediaService.DHash(image);
        var file = Record(path);
        await media.Read(file, Token);
        Assert.Equal("Decoded", file.MediaStatus);
        Assert.Equal(width, file.Width); Assert.Equal(height, file.Height);
        Assert.Equal(expectedHash, file.PerceptualHash);
        var metadataFile = Record(P("metadata.jpg"));
        await media.Read(metadataFile, Token);
        Assert.Equal(width, metadataFile.Width); Assert.Equal(height, metadataFile.Height);
        Assert.True(metadataFile.HasExif);
        Assert.Equal(new DateTime(2024, 8, 17, 12, 34, 56), metadataFile.Taken);
        Assert.Contains("TestCamera", metadataFile.Camera); Assert.Contains("ModelOne", metadataFile.Camera);
    }

    [Theory]
    [InlineData(".gif", MagickFormat.Gif)]
    [InlineData(".tiff", MagickFormat.Tiff)]
    public async Task MultiFrameImagesUseOnlyTheFirstFrame(string extension, MagickFormat format)
    {
        var path = P("frames" + extension);
        using var images = new MagickImageCollection();
        images.Add(Pattern());
        images.Add(new MagickImage(MagickColors.White, 160, 120));
        images.Write(path, format);
        var file = Record(path);
        await media.Read(file, Token);
        Assert.Equal("Decoded", file.MediaStatus);
        Assert.Equal(80, file.Width); Assert.Equal(40, file.Height);
    }

    [Fact]
    public async Task FileNamesAreNotInterpretedAsImageMagickFrameSelectors()
    {
        var path = P("photo[7].png");
        using (var image = Pattern())
        using (var stream = File.Create(path)) image.Write(stream, MagickFormat.Png);
        var file = Record(path);
        await media.Read(file, Token);
        Assert.Equal("Decoded", file.MediaStatus);
    }

    [Fact]
    public async Task NonRasterContentCannotInvokeOtherImageMagickCoders()
    {
        var path = P("disguised.jpg");
        await File.WriteAllTextAsync(path, "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/private\"/></svg>");
        var file = Record(path);
        await media.Read(file, Token);
        Assert.StartsWith("Unsupported:", file.MediaStatus);
        Assert.Contains("raster signature", file.MediaStatus);
        Assert.Null(file.PerceptualHash);
    }

    [Fact]
    public async Task LegacyMediaCacheRefreshesWithoutInvalidatingContentHashes()
    {
        using (var image = Pattern()) image.Write(P("image.png"), MagickFormat.Png);
        var index = new IndexService(media);
        var files = await index.Refresh(Options, Token);
        await index.FullHashes(files, Options, Token, crc: true);
        var file = Assert.Single(files);
        var expectedHash = file.PerceptualHash;
        var contentHash = file.FullHash;
        var partialHash = file.PartialHash;
        var crc = file.Crc32;
        file.MediaAnalysisVersion = 0;
        file.PerceptualHash = ~expectedHash!.Value;
        file.Width = file.Height = 1;
        IndexService.Persist(root, files);
        var refreshed = Assert.Single(await index.Refresh(Options, Token));
        Assert.Equal(expectedHash, refreshed.PerceptualHash);
        Assert.Equal(contentHash, refreshed.FullHash); Assert.Equal(partialHash, refreshed.PartialHash); Assert.Equal(crc, refreshed.Crc32);
        Assert.Equal(80, refreshed.Width); Assert.Equal(40, refreshed.Height);
        Assert.Equal(MediaService.AnalysisVersion, refreshed.MediaAnalysisVersion);
        refreshed.MediaStatus = "CachedMarker";
        IndexService.Persist(root, [refreshed]);
        Assert.Equal("CachedMarker", Assert.Single(await index.Refresh(Options, Token)).MediaStatus);
    }

    [Fact]
    public async Task FailedLegacyDecodeDoesNotLeaveAnOldPerceptualHash()
    {
        await File.WriteAllTextAsync(P("broken.jpg"), "not an image");
        var index = new IndexService(media);
        var files = await index.Refresh(Options, Token);
        var file = Assert.Single(files);
        file.MediaAnalysisVersion = 0;
        file.PerceptualHash = ulong.MaxValue;
        file.HasExif = true;
        file.Camera = "OldCamera";
        file.Taken = new DateTime(2000, 1, 1);
        file.Width = file.Height = 999;
        IndexService.Persist(root, files);
        var refreshed = Assert.Single(await index.Refresh(Options, Token));
        Assert.Null(refreshed.PerceptualHash); Assert.False(refreshed.HasExif); Assert.Null(refreshed.Taken);
        Assert.Equal("", refreshed.Camera); Assert.Equal(0, refreshed.Width); Assert.Equal(0, refreshed.Height);
        Assert.StartsWith("Unsupported:", refreshed.MediaStatus);
    }

    [Theory]
    [InlineData("photos")]
    [InlineData("classify")]
    public async Task LegacyVisualReportsMustBeRegeneratedButExactReportsRemainValid(string kind)
    {
        using (var image = Pattern()) image.Write(P("first.png"), MagickFormat.Png);
        File.Copy(P("first.png"), P("second.png"));
        var index = new IndexService(media);
        var reports = new ReportStore();
        var dedup = new DedupService(index, reports, media);
        var path = P("visual.csv");
        if (kind == "photos") await dedup.Photos(Options, path, 6, false, null, null, Token);
        else await new ClassificationService(index, new RuleEngine(), reports, media).Classify(Options, path, null, 0.7, false, 3, Token);
        var manifest = ReportStore.ReadJson<ReportManifest>(path + ".manifest.json");
        manifest.MediaAnalysisVersion = 0;
        ReportStore.WriteJson(path + ".manifest.json", manifest);
        Assert.Contains("regenerate", Assert.Throws<InvalidDataException>(() => ReportStore.ReadReviewed(path)).Message);
        await dedup.Dupes(Options, P("exact.csv"), "oldest", null, Token);
        var exact = ReportStore.ReadJson<ReportManifest>(P("exact.csv.manifest.json"));
        exact.MediaAnalysisVersion = 0;
        ReportStore.WriteJson(P("exact.csv.manifest.json"), exact);
        Assert.Equal(2, ReportStore.ReadReviewed(P("exact.csv")).Rows.Count);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAnUnsupportedImage()
    {
        using (var image = Pattern()) image.Write(P("image.png"), MagickFormat.Png);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.Read(Record(P("image.png")), cts.Token));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
