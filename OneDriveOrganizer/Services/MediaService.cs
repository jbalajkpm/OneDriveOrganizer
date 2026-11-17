using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using OneDriveOrganizer.Models;
using Serilog;

namespace OneDriveOrganizer.Services;

public sealed class MediaService
{
    public const int AnalysisVersion = 1;
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2" };
    public static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".webm" };
    public static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase) { ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2" };

    public async Task Read(FileRecord file, CancellationToken token)
    {
        if (!Images.Contains(file.Extension) && !Videos.Contains(file.Extension)) return;
        token.ThrowIfCancellationRequested();
        file.PerceptualHash = null;
        file.Width = file.Height = 0;
        file.HasExif = false;
        file.Taken = null;
        file.Camera = file.Gps = file.MediaStatus = "";
        file.Duration = null;
        file.ColorVariance = 0;
        file.MediaAnalysisVersion = AnalysisVersion;
        try
        {
            var metadata = ImageMetadataReader.ReadMetadata(file.Path);
            var exif = metadata.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            file.HasExif = exif != null;
            if (exif != null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date)) file.Taken = date;
            var camera = metadata.OfType<ExifIfd0Directory>().FirstOrDefault();
            file.Camera = string.Join(" ", camera?.GetDescription(ExifDirectoryBase.TagMake), camera?.GetDescription(ExifDirectoryBase.TagModel)).Trim();
            var gps = metadata.OfType<GpsDirectory>().FirstOrDefault();
            if (gps != null) file.Gps = string.Join(", ", gps.GetDescription(GpsDirectory.TagLatitude), gps.GetDescription(GpsDirectory.TagLongitude));
            if (file.Taken == null)
            {
                foreach (var dir in metadata.Where(d => d.Name.Contains("QuickTime", StringComparison.OrdinalIgnoreCase)))
                {
                    var tag = dir.Tags.FirstOrDefault(t => t.Name.Contains("Created", StringComparison.OrdinalIgnoreCase));
                    if (tag != null && dir.TryGetDateTime(tag.Type, out date)) { file.Taken = date; break; }
                }
            }
        }
        catch (Exception ex) when (ex is MetadataExtractor.ImageProcessingException or IOException or ArgumentException)
        {
            Log.Warning("Metadata unavailable for {Path}: {Error}", file.Path, ex.Message);
        }
        if (Videos.Contains(file.Extension) || file.Extension is ".heic" or ".heif" || Raw.Contains(file.Extension))
        {
            file.MediaStatus = "MetadataOnly: decoder/first frame unavailable";
            return;
        }
        try
        {
            using var image = await ReadRaster(file.Path, token);
            file.Width = checked((int)image.Width);
            file.Height = checked((int)image.Height);
            image.ColorSpace = ColorSpace.sRGB;
            image.Resize(new MagickGeometry(2048, 2048) { Greater = true }, FilterType.Catrom);
            token.ThrowIfCancellationRequested();
            file.PerceptualHash = DHash(image);
            using var sample = image.Clone();
            sample.Resize(new MagickGeometry(32, 32) { IgnoreAspectRatio = true }, FilterType.Catrom);
            using var pixels = sample.GetPixels();
            var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("Image has no RGB pixels");
            var colors = new List<double>(1024);
            for (int i = 0; i < rgb.Length; i += 3)
                colors.Add(Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2])) - Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2])));
            var mean = colors.Average();
            file.ColorVariance = colors.Average(x => (x - mean) * (x - mean));
            file.MediaStatus = "Decoded";
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or InvalidDataException)
        {
            token.ThrowIfCancellationRequested();
            file.PerceptualHash = null;
            file.MediaStatus = "Unsupported: " + ex.Message;
            Log.Warning("Image not decoded {Path}: {Error}", file.Path, ex.Message);
        }
    }

    public static ulong DHash(IMagickImage<byte> original)
    {
        using var image = original.Clone();
        image.ColorSpace = ColorSpace.sRGB;
        image.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true }, FilterType.Catrom);
        image.Grayscale(PixelIntensityMethod.Rec709Luma);
        using var pixels = image.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("Image has no RGB pixels");
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int offset = (y * 9 + x) * 3;
                if (rgb[offset] > rgb[offset + 3]) hash |= 1UL << (y * 8 + x);
            }
        return hash;
    }

    public static async Task<MagickImage> ReadRaster(string path, CancellationToken token)
    {
        await using var stream = HashService.Open(path);
        var header = new byte[16];
        int read = await stream.ReadAtLeastAsync(header, header.Length, false, token);
        var settings = new MagickReadSettings { Format = RasterFormat(header.AsSpan(0, read)), FrameIndex = 0, FrameCount = 1 };
        stream.Position = 0;
        var image = new MagickImage();
        try
        {
            image.Progress += (_, progress) => progress.Cancel = token.IsCancellationRequested;
            image.Warning += (_, warning) => Log.Warning("Image decoder warning {Path}: {Warning}", path, warning.Message);
            await image.PingAsync(stream, settings, token);
            if ((long)image.Width * image.Height > 100_000_000) throw new InvalidDataException("Image exceeds 100 megapixel decode limit");
            stream.Position = 0;
            await image.ReadAsync(stream, settings, token);
            image.AutoOrient();
            image.ColorSpace = ColorSpace.sRGB;
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static MagickFormat RasterFormat(ReadOnlySpan<byte> header)
    {
        // Force a raster coder and read streams, never ImageMagick pseudo-paths or external delegates.
        if (header.StartsWith(new byte[] { 255, 216, 255 })) return MagickFormat.Jpeg;
        if (header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return MagickFormat.Png;
        if (header.StartsWith("GIF8"u8)) return MagickFormat.Gif;
        if (header.StartsWith("BM"u8)) return MagickFormat.Bmp;
        if (header.StartsWith("II\x2a\0"u8) || header.StartsWith("MM\0\x2a"u8) || header.StartsWith("II\x2b\0"u8) || header.StartsWith("MM\0\x2b"u8)) return MagickFormat.Tiff;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return MagickFormat.WebP;
        throw new InvalidDataException("Unsupported raster signature; expected JPEG, PNG, GIF, BMP, TIFF or WebP");
    }

    public static DateTime? FilenameDate(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), @"(?<!\d)(?<y>20\d{2}|19\d{2})[-_]?(?<m>\d{2})[-_]?(?<d>\d{2})(?!\d)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success && DateTime.TryParseExact($"{match.Groups["y"]}{match.Groups["m"]}{match.Groups["d"]}", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    // Explicitly opted-in external decoders run without a shell and never write extracted media to disk.
    public async Task VideoFrame(FileRecord file, string ffmpeg, string ffprobe, CancellationToken token)
    {
        var probe = await Run(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "json", file.Path], token, 1_000_000);
        using var json = JsonDocument.Parse(probe);
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration) &&
            double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out var seconds)) file.Duration = seconds;
        var frame = await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", file.Path, "-frames:v", "1", "-vf", "scale=320:-1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"], token, 16_000_000);
        using var image = new MagickImage(frame, MagickFormat.Png);
        file.PerceptualHash = DHash(image);
        file.MediaAnalysisVersion = AnalysisVersion;
        file.MediaStatus = "FirstFrameOnly: review manually";
    }

    private static async Task<byte[]> Run(string executable, string[] arguments, CancellationToken token, int limit)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {executable}");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Decoder output limit exceeded");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(timeout.Token);
            var error = await errors;
            if (process.ExitCode != 0) throw new InvalidDataException($"Decoder failed: {error}");
            return output.ToArray();
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
