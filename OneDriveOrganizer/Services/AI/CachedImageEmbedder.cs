using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace OneDriveOrganizer.Services.AI;

public sealed class CachedImageEmbedder(IEmbeddingGenerator<ClipInput, Embedding<float>> generator, AiStore store, string modelId) : IImageEmbedder, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string ModelId { get; } = modelId;
    public async Task<float[]> ImageAsync(string path, CancellationToken token)
    {
        string hash = await HashService.Sha256(path, token);
        return await Get(hash, "image", new ClipInput(ImagePath: path), token);
    }
    public Task<float[]> TextAsync(string text, CancellationToken token) => Get(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "text", new ClipInput(Text: text), token);
    private async Task<float[]> Get(string hash, string kind, ClipInput input, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var cached = store.Vector(hash, kind, ModelId);
            if (cached != null) return cached;
            var response = await generator.GenerateAsync(new[] { input }, cancellationToken: token);
            if (response.Count != 1) throw new InvalidDataException("Expected exactly one CLIP embedding");
            var vector = Vectors.Normalize(response[0].Vector.Span);
            if (input.ImagePath != null && await HashService.Sha256(input.ImagePath, token) != hash) throw new IOException("Image changed during inference; embedding not cached");
            store.SaveVector(hash, kind, ModelId, vector);
            return vector;
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
