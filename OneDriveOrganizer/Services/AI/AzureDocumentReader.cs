using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Serilog;

namespace OneDriveOrganizer.Services.AI;

public sealed class AzureDocumentReader : IScannedDocumentReader, IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly Lazy<DefaultAzureCredential> credential = new(() => new DefaultAzureCredential());

    public static string Model(string text) => AiSchema.SensitiveText(text) && ClassificationService.HasWord(text, ["passport", "licence", "license", "identity", "pps"]) ? "prebuilt-idDocument" :
        ClassificationService.HasWord(text, ["invoice"]) ? "prebuilt-invoice" : ClassificationService.HasWord(text, ["receipt"]) ? "prebuilt-receipt" : "prebuilt-read";

    public async Task<AiReply> ReadAsync(AiRequest request, CancellationToken token)
    {
        var endpoint = AzureChatClassifier.Endpoint("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT");
        if (new FileInfo(request.Path).Length > 50 * 1024 * 1024) throw new InvalidDataException("Cloud PDF inspection is limited to 50 MiB");
        var uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/documentintelligence/documentModels/" + Model(request.Text) + ":analyze?api-version=2024-11-30&pages=1");
        using var start = new HttpRequestMessage(HttpMethod.Post, uri);
        start.Content = new StreamContent(HashService.Open(request.Path));
        start.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        await Authenticate(start, token);
        Log.Information("AI outbound {Hash} AzureDocumentIntelligence POST; tokens 0 (page-billed)", request.Hash);
        using var response = await http.SendAsync(start, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.Accepted || !response.Headers.TryGetValues("Operation-Location", out var locations) || !Uri.TryCreate(locations.Single(), UriKind.Absolute, out var operation) || operation.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("Invalid Document Intelligence operation URL");
        // Once accepted, polling errors must never escape as a retryable submission error.
        for (int attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            using var poll = new HttpRequestMessage(HttpMethod.Get, operation);
            await Authenticate(poll, token);
            Log.Information("AI outbound {Hash} AzureDocumentIntelligence GET; tokens 0", request.Hash);
            using var result = await http.SendAsync(poll, token);
            if (result.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) { await Task.Delay(TimeSpan.FromSeconds(2), token); continue; }
            if (!result.IsSuccessStatusCode) throw new IOException($"Document Intelligence polling failed ({(int)result.StatusCode}); accepted request will not be resubmitted");
            var json = await result.Content.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(json);
            var status = document.RootElement.GetProperty("status").GetString();
            if (status == "succeeded")
            {
                string content = document.RootElement.GetProperty("analyzeResult").GetProperty("content").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Document Intelligence returned no first-page text");
                return new AiReply(content[..Math.Min(content.Length, 12000)]);
            }
            if (status == "failed") throw new IOException("Document Intelligence operation failed; inspect the provider operation");
        }
        throw new TimeoutException("Document Intelligence polling timed out; accepted request will not be resubmitted");
    }

    private async Task Authenticate(HttpRequestMessage message, CancellationToken token)
    {
        string? key = Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_KEY");
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Add("Ocp-Apim-Subscription-Key", key);
        else
        {
            var access = await credential.Value.GetTokenAsync(new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }), token);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        }
    }
    public void Dispose() => http.Dispose();
}
