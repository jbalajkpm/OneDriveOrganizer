using System.ClientModel;
using Serilog;

namespace OneDriveOrganizer.Services.AI;

public sealed class CloudEnricher(AiStore store, IDocumentClassifier classifier, ICaptioner captioner, IScannedDocumentReader reader,
    string chatProvider, string documentProvider, TimeSpan? interval = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime nextCall;
    public async Task<AiResult?> Enrich(AiRequest request, double confidence, double minimum, AiOptions options, CancellationToken token)
    {
        if (options.Mode != AiMode.Cloud || confidence >= minimum) return null;
        if ((request.Sensitive || AiSchema.SensitiveText(request.Text)) && !options.AllowSensitiveCloud)
        {
            Log.Information("AI privacy gate: {Hash} retained locally", request.Hash);
            return null;
        }
        await gate.WaitAsync(token);
        try
        {
            // Inspect the content-hash result before OCR: identical bytes must not incur another bill.
            var cached = store.Result(request.Hash, chatProvider);
            if (cached != null) return AiSchema.Parse(cached);
            if (request.ScannedPdf)
            {
                var text = await Call(request.Hash, documentProvider, () => reader.ReadAsync(request, token), token);
                request = request with { Text = text };
                if (AiSchema.SensitiveText(text) && !options.AllowSensitiveCloud)
                {
                    Log.Warning("OCR detected sensitive content; chat upload blocked for {Hash}", request.Hash);
                    return new AiResult("Documents", "Sensitive", 0, "", true, "OCR identified sensitive content; further cloud enrichment blocked");
                }
            }
            var json = await Call(request.Hash, chatProvider, () => request.DetectedType == "Image" ? captioner.CaptionAsync(request, token) : classifier.ClassifyAsync(request, token), token);
            return AiSchema.Parse(json);
        }
        finally { gate.Release(); }
    }

    private async Task<string> Call(string hash, string provider, Func<Task<AiReply>> action, CancellationToken token)
    {
        var cached = store.Result(hash, provider);
        if (cached != null) return cached;
        store.Begin(hash, provider);
        for (int attempt = 0; ; attempt++)
        {
            var delay = nextCall - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            nextCall = DateTime.UtcNow + (interval ?? TimeSpan.FromSeconds(1));
            Log.Information("AI outbound {Hash} {Provider} attempt {Attempt}; tokens pending", hash, provider, attempt + 1);
            try
            {
                var result = await action();
                // Cache even malformed output: validation must not silently resubmit the same bytes.
                store.Complete(hash, provider, result.Json);
                Log.Information("AI completed {Hash} {Provider}; input tokens {InputTokens}, output tokens {OutputTokens}", hash, provider, result.InputTokens, result.OutputTokens);
                return result.Json;
            }
            catch (Exception ex) when (attempt < 3 && (ex is ClientResultException { Status: 429 or 503 } || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable }))
            {
                Log.Warning("AI throttled/unavailable {Hash} {Provider}; backoff attempt {Attempt}", hash, provider, attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble()), token);
            }
        }
    }
    public void Dispose() => gate.Dispose();
}
