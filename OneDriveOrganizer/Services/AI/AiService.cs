using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using Serilog;

namespace OneDriveOrganizer.Services.AI;

public sealed class AiService
{
    public AiSession? Open(string root, AiOptions options)
    {
        options.Validate();
        if (options.Mode == AiMode.Off) return null;
        var generator = new OnnxClipGenerator(options.Models);
        try
        {
            var store = new AiStore(root);
            var embedder = new CachedImageEmbedder(generator, store, generator.ModelId);
            return new AiSession(options, store, embedder, generator);
        }
        catch { generator.Dispose(); throw; }
    }
}

public sealed class AiSession(AiOptions options, AiStore store, IImageEmbedder embedder, IDisposable? generator = null, CloudEnricher? cloudOverride = null) : IDisposable
{
    private CloudEnricher? cloud = cloudOverride;
    private IChatClient? chat;
    private AzureDocumentReader? reader;
    public AiOptions Options { get; } = options;

    public async Task<Dictionary<string, float[]>> IndexImages(List<FileRecord> files, ScanOptions scan, CancellationToken token)
    {
        var vectors = new Dictionary<string, float[]>(PathPolicy.Comparer);
        var policy = new PathPolicy(scan);
        var candidates = files.Where(f => f.MediaStatus == "Decoded" && !MediaService.Videos.Contains(f.Extension)).ToList();
        int count = 0;
        foreach (var file in candidates)
        {
            token.ThrowIfCancellationRequested();
            IndexService.AssertUnchanged(file, policy);
            var cached = file.FullHash == null ? null : store.Vector(file.FullHash, "image", embedder.ModelId);
            if (cached != null) vectors[file.Path] = cached;
            else
            {
                vectors[file.Path] = await embedder.ImageAsync(file.Path, token);
                file.FullHash = await HashService.Sha256(file.Path, token);
            }
            IndexService.AssertUnchanged(file, policy);
            if (++count % 25 == 0 || count == candidates.Count) Log.Information("CLIP embeddings: {Done}/{Total}", count, candidates.Count);
        }
        IndexService.Persist(scan.Root, files);
        return vectors;
    }

    public async Task<(Rule Rule, double Confidence, bool Sensitive)> Label(string path, List<Rule> rules, CancellationToken token)
    {
        var labels = rules.Where(r => !string.IsNullOrWhiteSpace(r.AiLabel)).ToList();
        if (labels.Count is < 2 or > 100) throw new InvalidDataException("Local AI needs 2..100 aiLabel rules");
        var image = await embedder.ImageAsync(path, token);
        var scores = new List<(Rule Rule, double Score)>();
        foreach (var rule in labels) scores.Add((rule, Vectors.Cosine(image, await embedder.TextAsync("a photo of " + rule.AiLabel, token))));
        double max = scores.Max(s => s.Score);
        double denominator = scores.Sum(s => Math.Exp((s.Score - max) * 100));
        var best = scores.MaxBy(s => s.Score);
        bool sensitive = scores.Any(s => s.Rule.Sensitive && Math.Exp((s.Score - max) * 100) / denominator >= 0.1);
        return (best.Rule, 1 / denominator, sensitive);
    }

    public async Task<AiResult?> Enrich(AiRequest request, double confidence, double minimum, CancellationToken token)
    {
        if (Options.Mode != AiMode.Cloud || confidence >= minimum) return null;
        if ((request.Sensitive || AiSchema.SensitiveText(request.Text)) && !Options.AllowSensitiveCloud)
        {
            Log.Information("AI privacy gate: {Hash} retained locally", request.Hash);
            return null;
        }
        if (cloud == null)
        {
            chat = AzureChatClassifier.CreateClient();
            var classifier = new AzureChatClassifier(chat);
            reader = new AzureDocumentReader();
            string Identity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            string chatId = "AzureOpenAI-v1-" + Identity(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") + ":" + (Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini"));
            string documentId = "AzureDocumentIntelligence-v1-" + Identity(Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT") ?? "");
            cloud = new CloudEnricher(store, classifier, classifier, reader, chatId, documentId);
        }
        await using var source = HashService.Open(request.Path);
        if (await HashService.Sha256(request.Path, token) != request.Hash) throw new IOException("File changed before cloud enrichment");
        var result = await cloud.Enrich(request, confidence, minimum, Options, token);
        if (await HashService.Sha256(request.Path, token) != request.Hash) throw new IOException("File changed during cloud enrichment");
        return result;
    }

    public async Task<List<SearchHit>> Search(List<FileRecord> files, ScanOptions scan, string query, int topK, CancellationToken token)
    {
        if (topK is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(topK), "Top K must be 1..1000");
        var text = await embedder.TextAsync(query, token);
        var hits = new List<SearchHit>();
        var policy = new PathPolicy(scan);
        foreach (var file in files.Where(f => f.MediaStatus == "Decoded"))
        {
            token.ThrowIfCancellationRequested(); IndexService.AssertUnchanged(file, policy);
            string hash = await HashService.Sha256(file.Path, token);
            var vector = store.Vector(hash, "image", embedder.ModelId);
            if (vector != null) hits.Add(new SearchHit(file.Path, Vectors.Cosine(text, vector)));
        }
        if (hits.Count == 0) Log.Warning("No current embeddings for this model; run index with local AI first");
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Path, PathPolicy.Comparer).Take(topK).ToList();
    }

    public void Dispose() { cloud?.Dispose(); chat?.Dispose(); reader?.Dispose(); (embedder as IDisposable)?.Dispose(); generator?.Dispose(); store.Dispose(); }
}
