using System.Text.Json;
using ImageMagick;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;
using OneDriveOrganizer.Services.AI;

namespace OneDriveOrganizer.Tests;

public sealed class AiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-ai-tests-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => CancellationToken.None;
    private ScanOptions Scan => new(root, [], true, 2);
    private const string Valid = """{"category":"Documents","subcategory":"Reference","confidence":0.95,"suggestedFileName":"descriptive-notes","isSensitive":false,"reason":"Document content"}""";
    public AiTests() => Directory.CreateDirectory(root);
    private string P(string name) => Path.Combine(root, name);
    private string Put(string name, string content) { string path = P(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    private static float[] Vector(double cosine = 1) { var v = new float[512]; v[0] = (float)cosine; v[1] = (float)Math.Sqrt(1 - cosine * cosine); return v; }
    private static AiRequest Request(bool sensitive = false, bool scanned = false) => new("unused.pdf", "ABC123", scanned ? "PDF" : "Text", "ordinary content", sensitive, scanned);
    private static CloudEnricher Cloud(AiStore store, FakeChat chat, FakeReader reader) { var classifier = new AzureChatClassifier(chat); return new(store, classifier, classifier, reader, "test-chat-v1", "test-ocr-v1", TimeSpan.Zero); }

    [Fact]
    public async Task EmbeddingsAreContentCachedSingleFlightAndPersistAs512FloatBlob()
    {
        string a = Put("a.jpg", "identical bytes"), b = Put("b.jpg", "identical bytes");
        string hash = await HashService.Sha256(a, Token);
        var generator = new FakeEmbeddings();
        using (var store = new AiStore(root))
        using (var embedder = new CachedImageEmbedder(generator, store, "model-a"))
        {
            var results = await Task.WhenAll(embedder.ImageAsync(a, Token), embedder.ImageAsync(b, Token));
            Assert.All(results, v => Assert.Equal(512, v.Length)); Assert.Equal(1, generator.Calls);
            await embedder.TextAsync("pets", Token); await embedder.TextAsync("pets", Token);
            Assert.Equal(2, generator.Calls);
            Assert.Null(store.Vector(hash, "image", "different-model"));
        }
        using (var store = new AiStore(root)) Assert.Equal(Vector(), store.Vector(hash, "image", "model-a"));
        using var connection = new SqliteConnection($"Data Source={P(".odo\\index.db")}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT length(vector) FROM ai_vectors WHERE kind='image'";
        Assert.Equal(2048L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ChangedBytesGetNewEmbeddingAndInvalidDimensionsAreRejected()
    {
        var path = Put("image.jpg", "before");
        using var store = new AiStore(root); var generator = new FakeEmbeddings();
        using var embedder = new CachedImageEmbedder(generator, store, "model");
        await embedder.ImageAsync(path, Token); File.WriteAllText(path, "after"); await embedder.ImageAsync(path, Token);
        Assert.Equal(2, generator.Calls);
        Assert.Throws<InvalidDataException>(() => Vectors.Normalize(new float[511]));
        Assert.Throws<InvalidDataException>(() => Vectors.Normalize(new float[512]));
        var invalid = Vector(); invalid[2] = float.NaN;
        Assert.Throws<InvalidDataException>(() => store.SaveVector("x", "image", "model", invalid));
    }

    [Fact]
    public async Task ChatResultsAreCachedAcrossPathsConcurrentCallsAndRestarts()
    {
        var chat = new FakeChat(); var reader = new FakeReader();
        using (var store = new AiStore(root))
        using (var cloud = Cloud(store, chat, reader))
        {
            var results = await Task.WhenAll(cloud.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token), cloud.Enrich(Request() with { Path = "different.pdf" }, 0.1, 0.7, new(AiMode.Cloud), Token));
            Assert.All(results, result => Assert.Equal("descriptive-notes", result!.SuggestedFileName));
            Assert.Equal(1, chat.Calls); Assert.Equal(0, reader.Calls);
            Assert.NotNull(chat.Options?.ResponseFormat);
        }
        using var restarted = new AiStore(root); using var next = Cloud(restarted, chat, reader);
        Assert.NotNull(await next.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token)); Assert.Equal(1, chat.Calls);
    }

    [Theory]
    [InlineData(AiMode.Off, false, 0.1)]
    [InlineData(AiMode.Local, false, 0.1)]
    [InlineData(AiMode.Cloud, true, 0.1)]
    [InlineData(AiMode.Cloud, false, 0.7)]
    public async Task PrivacyAndConfidenceGateRunBeforeEitherProvider(AiMode mode, bool sensitive, double confidence)
    {
        using var store = new AiStore(root); var chat = new FakeChat(); var reader = new FakeReader(); using var cloud = Cloud(store, chat, reader);
        Assert.Null(await cloud.Enrich(Request(sensitive, true), confidence, 0.7, new(mode), Token));
        Assert.Equal(0, chat.Calls); Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ExplicitSensitiveOptInAllowsOcrAndChatOnce()
    {
        using var store = new AiStore(root); var chat = new FakeChat(); var reader = new FakeReader { Text = "passport number" }; using var cloud = Cloud(store, chat, reader);
        var options = new AiOptions(AiMode.Cloud, AllowSensitiveCloud: true);
        Assert.NotNull(await cloud.Enrich(Request(true, true), 0.1, 0.7, options, Token));
        Assert.NotNull(await cloud.Enrich(Request(true, true), 0.1, 0.7, options, Token));
        Assert.Equal(1, chat.Calls); Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task OcrDiscoveredSensitivityBlocksChatAndIsReused()
    {
        using var store = new AiStore(root); var chat = new FakeChat(); var reader = new FakeReader { Text = "patient prescription" }; using var cloud = Cloud(store, chat, reader);
        var result = await cloud.Enrich(Request(scanned: true), 0.1, 0.7, new(AiMode.Cloud), Token);
        Assert.True(result!.IsSensitive); Assert.Equal(0, result.Confidence);
        await cloud.Enrich(Request(scanned: true), 0.1, 0.7, new(AiMode.Cloud), Token);
        Assert.Equal(0, chat.Calls); Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task InvalidResponsesAreCachedAndAmbiguousRequestsAreNotReplayed()
    {
        using var store = new AiStore(root); var chat = new FakeChat { Json = "{}" }; var reader = new FakeReader(); using var cloud = Cloud(store, chat, reader);
        await Assert.ThrowsAsync<InvalidDataException>(() => cloud.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => cloud.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token));
        Assert.Equal(1, chat.Calls);
        store.Begin("pending", "test-chat-v1");
        await Assert.ThrowsAsync<IOException>(() => cloud.Enrich(Request() with { Hash = "pending" }, 0.1, 0.7, new(AiMode.Cloud), Token));
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task ThrottlingBacksOffAndCachesTheSuccessfulRetry()
    {
        using var store = new AiStore(root); var chat = new FakeChat { Throttles = 1 }; using var cloud = Cloud(store, chat, new FakeReader());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await cloud.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token));
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(2)); Assert.Equal(2, chat.Calls);
        await cloud.Enrich(Request(), 0.1, 0.7, new(AiMode.Cloud), Token); Assert.Equal(2, chat.Calls);
    }

    [Theory]
    [InlineData("\"confidence\":0.95", "\"confidence\":1.1")]
    [InlineData("\"confidence\":0.95", "\"confidence\":\"0.95\"")]
    [InlineData("\"isSensitive\":false", "\"isSensitive\":\"false\"")]
    [InlineData("\"subcategory\":\"Reference\"", "\"subcategory\":\"../escape\"")]
    [InlineData("\"suggestedFileName\":\"descriptive-notes\"", "\"suggestedFileName\":\"CON\"")]
    [InlineData("\"category\":\"Documents\"", "\"category\":\"Bogus\"")]
    [InlineData("\"category\":\"Documents\",", "")]
    [InlineData("\"category\":\"Documents\",", "\"extra\":true,\"category\":\"Documents\",")]
    [InlineData("\"category\":\"Documents\",", "\"category\":\"Documents\",\"category\":\"Documents\",")]
    public void StrictSchemaRejectsUnsafeMissingExtraDuplicateAndWrongTypeFields(string from, string to) => Assert.Throws<InvalidDataException>(() => AiSchema.Parse(Valid.Replace(from, to)));

    [Theory]
    [InlineData("invoice", "prebuilt-invoice")]
    [InlineData("receipt", "prebuilt-receipt")]
    [InlineData("passport", "prebuilt-idDocument")]
    [InlineData("notes", "prebuilt-read")]
    public void ScannedDocumentModelSelection(string text, string expected) => Assert.Equal(expected, AzureDocumentReader.Model(text));

    [Fact]
    public void CosineGroupsMergeDHashWithoutLosingCrossGroupNeighbors()
    {
        var files = Enumerable.Range(0, 3).Select(i => new FileRecord { Path = i.ToString() }).ToList();
        var union = new UnionFind(3); union.Join(0, 1);
        var vectors = new Dictionary<string, float[]> { ["0"] = Vector(), ["1"] = Vector(0), ["2"] = Vector(0.94) };
        CosineGrouping.Merge(files, vectors, union, 0.92, Token);
        Assert.Equal(union.Find(0), union.Find(2)); Assert.Equal(union.Find(1), union.Find(2));
        union = new UnionFind(3); CosineGrouping.Merge(files, vectors, union, 0.95, Token);
        Assert.NotEqual(union.Find(0), union.Find(2));
    }

    [Fact]
    public void MetricTreeMatchesBruteForceAcrossRandomEmbeddings()
    {
        var random = new Random(42);
        var files = Enumerable.Range(0, 150).Select(i => new FileRecord { Path = i.ToString() }).ToList();
        var vectors = files.ToDictionary(f => f.Path, _ => { var v = new float[512]; for (int i = 0; i < 8; i++) v[i] = (float)(random.NextDouble() * 2 - 1); return Vectors.Normalize(v); });
        var expected = new UnionFind(files.Count); var actual = new UnionFind(files.Count);
        for (int i = 0; i < files.Count; i++) for (int j = 0; j < i; j++) if (Vectors.Cosine(vectors[files[i].Path], vectors[files[j].Path]) >= 0.85) expected.Join(i, j);
        CosineGrouping.Merge(files, vectors, actual, 0.85, Token);
        for (int i = 0; i < files.Count; i++) for (int j = 0; j < i; j++) Assert.Equal(expected.Find(i) == expected.Find(j), actual.Find(i) == actual.Find(j));
    }

    [Fact]
    public async Task LocalLabelsSearchAndImageCacheAreIntegrated()
    {
        var imagePath = P("image.png"); using (var image = new MagickImage(MagickColors.Red, 32, 32)) image.Write(imagePath);
        var media = new MediaService(); var index = new IndexService(media); var files = await index.Refresh(Scan, Token);
        var store = new AiStore(root); var generator = new FakeEmbeddings { Generate = value => value.ImagePath != null || value.Text!.EndsWith("pets") ? Vector() : Vector(0) };
        var embedder = new CachedImageEmbedder(generator, store, "mock-clip"); using var ai = new AiSession(new(), store, embedder);
        await ai.IndexImages(files, Scan, Token);
        var rules = new RuleEngine().Load(null);
        Assert.Equal(10, rules.Count(r => r.AiLabel != null));
        Assert.All(rules.Where(r => r.AiLabel != null), r => Assert.False(RuleEngine.Matches(r, files[0], [], "")));
        var label = await ai.Label(imagePath, rules, Token); Assert.Equal("pets", label.Rule.AiLabel); Assert.True(label.Confidence > 0.99); Assert.False(label.Sensitive);
        var hit = Assert.Single(await ai.Search(files, Scan, "pets", 1, Token)); Assert.Equal(imagePath, hit.Path); Assert.InRange(hit.Score, 0.99999, 1);
        var classifier = new ClassificationService(index, new RuleEngine(), new ReportStore(), media);
        var plan = await classifier.Classify(Scan, P("plan.csv"), null, 0.7, false, 3, Token, ai);
        Assert.Equal("Photos/Pets", Assert.Single(plan).Category);
    }

    [Fact]
    public async Task CloudClassificationProducesReviewedReversibleRenameAndCachesIdenticalBytes()
    {
        string first = Put("one.txt", "ordinary document content"), second = Put("two.txt", "ordinary document content");
        var rules = P(".odo\\rules.json"); Directory.CreateDirectory(Path.GetDirectoryName(rules)!);
        ReportStore.WriteJson(rules, new[] { new Rule { Name = "Low", Extensions = [".txt"], Target = "Misc", Confidence = 0.1 } });
        var store = new AiStore(root); var chat = new FakeChat(); var cloud = Cloud(store, chat, new FakeReader());
        using var ai = new AiSession(new(AiMode.Cloud), store, new CachedImageEmbedder(new FakeEmbeddings(), store, "mock"), cloudOverride: cloud);
        var media = new MediaService(); var classifier = new ClassificationService(new(media), new(), new(), media);
        string output = P("plan.csv"), journal = P("rename-undo.json");
        var rows = await classifier.Classify(Scan, output, rules, 0.7, false, 3, Token, ai);
        Assert.Equal(2, rows.Count); Assert.Equal(1, chat.Calls); Assert.All(rows, r => Assert.Equal("descriptive-notes", r.SuggestedFileName));
        var execution = new ExecutionService();
        await execution.Apply(output, true, false, false, false, false, journal, Token, rename: true);
        Assert.True(File.Exists(first)); Assert.False(File.Exists(journal));
        await Assert.ThrowsAsync<ArgumentException>(() => execution.Apply(output, true, true, false, false, false, journal, Token, rename: true));
        await execution.Apply(output, true, true, true, false, false, journal, Token, rename: true);
        Assert.False(File.Exists(first)); Assert.False(File.Exists(second));
        var entries = ReportStore.ReadJson<UndoJournal>(journal).Entries;
        Assert.Equal(2, entries.Count); Assert.NotEqual(entries[0].Destination, entries[1].Destination);
        Assert.All(entries, e => { Assert.Equal(".txt", Path.GetExtension(e.Destination)); Assert.Equal(root, Path.GetDirectoryName(e.Destination)); });
        await execution.Undo(journal, true, true, Token); Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
    }

    [Fact]
    public void LegacyCsvMayOmitOnlyNewAiColumns()
    {
        var row = new ReportRow { Path = "a", LastWriteUtc = DateTime.UtcNow };
        string path = P("legacy.csv"); ReportStore.WriteCsv(path, new[] { row });
        var lines = File.ReadAllLines(path).Select(line => string.Join(',', line.Split(',').SkipLast(2))).ToArray(); File.WriteAllLines(path, lines);
        Assert.Equal(row, Assert.Single(ReportStore.ReadCsv(path)));
        File.WriteAllLines(path, lines.Select(line => string.Join(',', line.Split(',').Skip(1))));
        Assert.ThrowsAny<Exception>(() => ReportStore.ReadCsv(path));
    }

    [Fact]
    public void MissingModelsFailExplicitlyAndOffNeedsNoAssets()
    {
        Assert.Null(new AiService().Open(root, new(AiMode.Off)));
        Assert.Throws<FileNotFoundException>(() => new AiService().Open(root, new(AiMode.Local, P("missing"))));
    }

    [Fact]
    public void ClipTokenizerPreservesHashMergesBosEosPaddingAndTruncation()
    {
        var vocabulary = new Dictionary<string, int> { ["<|startoftext|>"] = 49406, ["<|endoftext|>"] = 49407, ["cat</w>"] = 300, ["##</w>"] = 301, ["i</w>"] = 302 };
        string vocab = Put("vocab.json", JsonSerializer.Serialize(vocabulary));
        string merges = Put("merges.txt", "#version: 0.2\nc a\nca t</w>\n# #</w>\n");
        var tokenizer = new ClipTokenizer(vocab, merges);
        var (ids, mask) = tokenizer.Encode(" CAT  ## ");
        Assert.Equal(new long[] { 49406, 300, 301, 49407 }, ids.Take(4)); Assert.All(ids.Skip(4), id => Assert.Equal(49407, id));
        Assert.Equal(4, mask.Sum());
        (ids, mask) = tokenizer.Encode(string.Join(' ', Enumerable.Repeat("I", 100)));
        Assert.Equal(77, ids.Length); Assert.Equal(49407, ids[^1]); Assert.Equal(77, mask.Sum());
        Assert.All(ids.Skip(1).SkipLast(1), id => Assert.Equal(302, id));
    }

    [ClipFact]
    public async Task RealOnnxModelsProduceFinite512DimensionalVectorsAndSemanticSearch()
    {
        string red = P("red.png"), blue = P("blue.png");
        using (var image = new MagickImage(MagickColors.Red, 256, 256)) image.Write(red);
        using (var image = new MagickImage(MagickColors.Blue, 256, 256)) image.Write(blue);
        using var ai = new AiService().Open(root, new(AiMode.Local, Environment.GetEnvironmentVariable("ONEDRIVE_ORGANIZER_TEST_CLIP_MODELS")))!;
        var files = await new IndexService(new()).Refresh(Scan, Token);
        var vectors = await ai.IndexImages(files, Scan, Token);
        Assert.Equal(2, vectors.Count);
        Assert.All(vectors.Values, vector => { Assert.Equal(512, vector.Length); Assert.All(vector, x => Assert.True(float.IsFinite(x))); Assert.InRange(vector.Sum(x => (double)x * x), 0.99999, 1.00001); });
        var hit = Assert.Single(await ai.Search(files, Scan, "a solid red image", 1, Token));
        Assert.Equal(red, hit.Path);
        var label = await ai.Label(red, new RuleEngine().Load(null), Token);
        Assert.InRange(label.Confidence, 0, 1);
        if (Environment.GetEnvironmentVariable("ONEDRIVE_ORGANIZER_TEST_EXE") is { Length: > 0 } executable)
        {
            async Task<string> Run(params string[] args)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                string text = await output, errors = await error;
                Assert.True(process.ExitCode == 0, text + errors);
                return text;
            }
            string models = Environment.GetEnvironmentVariable("ONEDRIVE_ORGANIZER_TEST_CLIP_MODELS")!;
            await Run("index", root, "--clip-model-dir", models, "--out", P("published-index.csv"));
            Assert.Equal(2, ReportStore.ReadCsv(P("published-index.csv")).Count);
            var search = await Run("search", root, "a solid red image", "--clip-model-dir", models, "--top-k", "1");
            Assert.Contains(red, search);
            await Run("photos", root, "--clip-model-dir", models, "--out", P("published-photos.csv"));
            await Run("classify", root, "--clip-model-dir", models, "--out", P("published-plan.csv"));
            await Run("rename", P("published-plan.csv"));
            await Run("index", root, "--ai", "off", "--out", P("published-off.csv"));
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    private sealed class FakeEmbeddings : IEmbeddingGenerator<ClipInput, Embedding<float>>
    {
        public int Calls;
        public Func<ClipInput, float[]> Generate { get; set; } = _ => Vector();
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<ClipInput> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values) { Calls++; result.Add(new Embedding<float>(Generate(value))); }
            return result;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class FakeChat : IChatClient
    {
        public int Calls;
        public int Throttles;
        public string Json { get; set; } = Valid;
        public ChatOptions? Options;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++; Options = options;
            if (Calls <= Throttles) throw new HttpRequestException("Rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, Json)) { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20 } };
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class FakeReader : IScannedDocumentReader
    {
        public int Calls;
        public string Text { get; set; } = "ordinary content";
        public Task<AiReply> ReadAsync(AiRequest request, CancellationToken token) { Calls++; return Task.FromResult(new AiReply(Text)); }
    }
}

public sealed class ClipFactAttribute : FactAttribute
{
    public ClipFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONEDRIVE_ORGANIZER_TEST_CLIP_MODELS"))) Skip = "Set ONEDRIVE_ORGANIZER_TEST_CLIP_MODELS to run real ONNX inference with local assets";
    }
}
