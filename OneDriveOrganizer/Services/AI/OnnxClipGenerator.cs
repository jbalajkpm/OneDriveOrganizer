using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OneDriveOrganizer.Services.AI;

public sealed class OnnxClipGenerator : IEmbeddingGenerator<ClipInput, Embedding<float>>
{
    private readonly InferenceSession vision;
    private readonly InferenceSession text;
    private readonly ClipTokenizer tokenizer;
    public string ModelId { get; }

    public OnnxClipGenerator(string directory)
    {
        string[] names = ["vision_model.onnx", "text_model.onnx", "vocab.json", "merges.txt"];
        foreach (var name in names)
            if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException($"Local AI needs CLIP ViT-B/32 assets in {directory} ({name} missing). See README model setup, or explicitly use --ai off.");
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes("clip-b32-rgb224-bicubic-eos-pad-v1"));
        foreach (var name in names) { using var stream = File.OpenRead(Path.Combine(directory, name)); fingerprint.AppendData(SHA256.HashData(stream)); }
        ModelId = Convert.ToHexString(fingerprint.GetHashAndReset());
        tokenizer = new ClipTokenizer(Path.Combine(directory, names[2]), Path.Combine(directory, names[3]));
        using var settings = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount), InterOpNumThreads = 1 };
        vision = new InferenceSession(Path.Combine(directory, names[0]), settings);
        try
        {
            text = new InferenceSession(Path.Combine(directory, names[1]), settings);
            if (!vision.InputMetadata.ContainsKey("pixel_values") || !vision.OutputMetadata.ContainsKey("image_embeds") ||
                !text.InputMetadata.ContainsKey("input_ids") || !text.OutputMetadata.ContainsKey("text_embeds"))
                throw new InvalidDataException("Use separate CLIP vision/text ONNX exports with pixel_values/image_embeds and input_ids/text_embeds (512-D projected outputs).");
        }
        catch { vision.Dispose(); text?.Dispose(); throw; }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<ClipInput> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((value.ImagePath == null) == (value.Text == null)) throw new ArgumentException("Supply exactly one image or text input");
            var inputs = new List<NamedOnnxValue>();
            InferenceSession session;
            string output;
            if (value.ImagePath != null)
            {
                using var image = await MediaService.ReadRaster(value.ImagePath, cancellationToken);
                double scale = 224d / Math.Min(image.Width, image.Height);
                if (image.Width * scale * image.Height * scale > 50_000_000) throw new InvalidDataException("CLIP resize exceeds the 50 megapixel working-image limit");
                image.Resize(new MagickGeometry((uint)Math.Max(224, (int)(image.Width * scale)), (uint)Math.Max(224, (int)(image.Height * scale))) { IgnoreAspectRatio = true }, FilterType.Catrom);
                image.Crop(224, 224, Gravity.Center); image.ResetPage(); image.Alpha(AlphaAction.Off);
                using var pixels = image.GetPixels();
                var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("Image has no pixels");
                float[] mean = [0.48145466f, 0.4578275f, 0.40821073f], std = [0.26862954f, 0.26130258f, 0.27577711f];
                var tensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
                for (int c = 0; c < 3; c++) for (int y = 0; y < 224; y++) for (int x = 0; x < 224; x++)
                    tensor[0, c, y, x] = (rgb[(y * 224 + x) * 3 + c] / 255f - mean[c]) / std[c];
                inputs.Add(NamedOnnxValue.CreateFromTensor("pixel_values", tensor)); session = vision; output = "image_embeds";
            }
            else
            {
                var (ids, mask) = tokenizer.Encode(value.Text!);
                inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { 1, ClipTokenizer.Length })));
                if (text.InputMetadata.ContainsKey("attention_mask")) inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, ClipTokenizer.Length })));
                session = text; output = "text_embeds";
            }
            using var run = new RunOptions();
            using var registration = cancellationToken.Register(() => run.Terminate = true);
            try
            {
                using var results = session.Run(inputs, new[] { output }, run);
                embeddings.Add(new Embedding<float>(Vectors.Normalize(results.First().AsTensor<float>().ToArray())));
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        }
        return embeddings;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { vision.Dispose(); text.Dispose(); }
}
