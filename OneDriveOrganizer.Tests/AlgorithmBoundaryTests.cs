using System.Text;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;

namespace OneDriveOrganizer.Tests;

public sealed class AlgorithmBoundaryTests
{
    private static FileRecord Photo(ulong hash) => new() { Extension = ".jpg", PerceptualHash = hash };

    [Theory]
    [InlineData(0, 0UL, true)]
    [InlineData(0, 1UL, false)]
    [InlineData(6, 63UL, true)]
    [InlineData(6, 127UL, false)]
    [InlineData(16, 65535UL, true)]
    public void GroupingHonorsExactThreshold(int threshold, ulong hash, bool expected)
    {
        var result = VisualGrouping.Group([Photo(0), Photo(hash)], threshold, CancellationToken.None);
        Assert.Equal(expected, result.Find(0) == result.Find(1));
    }

    [Fact]
    public void AboveTwentyThousandImagesKeepsCrossPrefixNeighbors()
    {
        var images = Enumerable.Range(0, 20_001).Select(_ => Photo(ulong.MaxValue)).ToArray();
        images[0] = Photo(0);
        images[1] = Photo(1UL << 63);
        images[2] = Photo(255);
        var result = VisualGrouping.Group(images, 6, CancellationToken.None);
        Assert.Equal(result.Find(0), result.Find(1));
        Assert.NotEqual(result.Find(0), result.Find(2));
        Assert.NotEqual(result.Find(0), result.Find(3));
        Assert.Equal(result.Find(3), result.Find(20_000));
    }

    [Fact]
    public void UnionFindIncludesTransitiveNeighbors()
    {
        var result = VisualGrouping.Group([Photo(0), Photo(7), Photo(63)], 3, CancellationToken.None);
        Assert.Equal(result.Find(0), result.Find(2));
        Assert.Equal(6, HashService.Hamming(0, 63));
    }

    [Fact]
    public void VideoCandidatesRequireDurationAndCannotMergeWithPhotos()
    {
        FileRecord Video(double? duration) => new() { Extension = ".mov", Duration = duration, PerceptualHash = 0 };
        var result = VisualGrouping.Group([Video(10), Video(11), Video(13), Video(null), Photo(0)], 6, CancellationToken.None);
        Assert.Equal(result.Find(0), result.Find(1));
        Assert.NotEqual(result.Find(0), result.Find(2));
        Assert.NotEqual(result.Find(0), result.Find(3));
        Assert.NotEqual(result.Find(0), result.Find(4));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ArchiveHashRejectsLengthMismatch(long expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        await Assert.ThrowsAsync<InvalidDataException>(() => HashService.BoundedSha256(stream, expected, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveHashMatchesKnownSha256()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", await HashService.BoundedSha256(stream, 3, CancellationToken.None));
    }

    [Fact]
    public void InvalidUtf8IsNotMisclassifiedAsText()
    {
        Assert.Equal("Unknown", ClassificationService.DetectType([0xC0, 0xC0, 0xFF], "note.txt"));
    }

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public void BomEncodedTextTakesPrecedenceOverAudioSyncBytes(int codePage)
    {
        var encoding = Encoding.GetEncoding(codePage);
        var header = encoding.GetPreamble().Concat(encoding.GetBytes("Diagnostic report\r\nName\tValue\r\n\u03A9 \u4E2D \U0001F642")).ToArray();
        Assert.Equal("Text", ClassificationService.DetectType(header, "diagnostic.txt"));
        Assert.Equal("Text", ClassificationService.DetectType(header, "misleading.mp3"));
        Assert.Equal("Text", ClassificationService.DetectType(header[..^1], "sample-ending-mid-character.txt"));
    }

    [Theory]
    [InlineData("49443304000000000000")]
    [InlineData("664C614300000022")]
    [InlineData("4F6767530002")]
    [InlineData("FFFB90640000")]
    [InlineData("FFFE400000000000")]
    [InlineData("FFF15080001FFC")]
    [InlineData("524946462400000057415645")]
    public void AudioSignaturesStillOverrideMisleadingTextExtensions(string hex)
    {
        Assert.Equal("Audio", ClassificationService.DetectType(Convert.FromHexString(hex), "misleading.txt"));
    }
}
