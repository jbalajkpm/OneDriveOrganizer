using OneDriveOrganizer.Models;

namespace OneDriveOrganizer.Services.AI;

public enum AiMode { Off, Local, Cloud }

public sealed record AiOptions(AiMode Mode = AiMode.Local, string? ModelDirectory = null, double CosineThreshold = 0.92, bool AllowSensitiveCloud = false)
{
    public string Models => Path.GetFullPath(ModelDirectory ?? Environment.GetEnvironmentVariable("ONEDRIVE_ORGANIZER_CLIP_MODELS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneDriveOrganizer", "models", "clip-vit-b32"));
    public void Validate()
    {
        if (!double.IsFinite(CosineThreshold) || CosineThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(CosineThreshold), "Cosine threshold must be 0..1");
    }
}

public sealed record ClipInput(string? ImagePath = null, string? Text = null);
public sealed record AiResult(string Category, string Subcategory, double Confidence, string SuggestedFileName, bool IsSensitive, string Reason);
public sealed record AiRequest(string Path, string Hash, string DetectedType, string Text, bool Sensitive, bool ScannedPdf = false);
public sealed record AiReply(string Json, long InputTokens = 0, long OutputTokens = 0);
public sealed record SearchHit(string Path, double Score);

public interface IImageEmbedder
{
    string ModelId { get; }
    Task<float[]> ImageAsync(string path, CancellationToken token);
    Task<float[]> TextAsync(string text, CancellationToken token);
}

public interface IDocumentClassifier
{
    Task<AiReply> ClassifyAsync(AiRequest request, CancellationToken token);
}

public interface ICaptioner
{
    Task<AiReply> CaptionAsync(AiRequest request, CancellationToken token);
}

public interface IScannedDocumentReader
{
    Task<AiReply> ReadAsync(AiRequest request, CancellationToken token);
}

public static class Vectors
{
    public const int Dimensions = 512;
    public static float[] Normalize(ReadOnlySpan<float> values)
    {
        if (values.Length != Dimensions) throw new InvalidDataException("CLIP must produce exactly 512 dimensions");
        double sum = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("Embedding contains non-finite values");
            sum += (double)value * value;
        }
        if (sum <= 0) throw new InvalidDataException("Embedding has zero magnitude");
        double length = Math.Sqrt(sum);
        var result = values.ToArray();
        for (int i = 0; i < result.Length; i++) result[i] = (float)(result[i] / length);
        return result;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != Dimensions || b.Length != Dimensions) throw new InvalidDataException("Embedding dimension mismatch");
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        if (!double.IsFinite(dot) || aa <= 0 || bb <= 0) throw new InvalidDataException("Invalid embedding");
        return Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1);
    }
}
