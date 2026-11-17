using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneDriveOrganizer.Services.AI;

public sealed class ClipTokenizer
{
    private readonly Dictionary<string, int> vocabulary;
    private readonly Dictionary<(string, string), int> ranks = new();
    private readonly Dictionary<byte, char> byteCharacters = new();
    private static readonly Regex Tokens = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    public const int Length = 77;

    public ClipTokenizer(string vocabularyPath, string mergesPath)
    {
        vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabularyPath)) ?? throw new InvalidDataException("Invalid CLIP vocabulary");
        if (vocabulary.GetValueOrDefault("<|startoftext|>") != 49406 || vocabulary.GetValueOrDefault("<|endoftext|>") != 49407)
            throw new InvalidDataException("Expected OpenAI CLIP ViT-B/32 vocabulary (BOS=49406, EOS=49407)");
        int rank = 0;
        foreach (var line in File.ReadLines(mergesPath).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#version:", StringComparison.Ordinal)))
        {
            var pair = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length != 2) throw new InvalidDataException("Invalid CLIP merge rule");
            ranks.Add((pair[0], pair[1]), rank++);
        }
        int escaped = 256;
        for (int b = 0; b < 256; b++)
            byteCharacters[(byte)b] = b is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 and <= 255 ? (char)b : (char)escaped++;
    }

    public (long[] Ids, long[] Mask) Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("Semantic text must be 1..16000 characters");
        text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text)).Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var ids = new List<long> { 49406 };
        foreach (Match match in Tokens.Matches(text))
        {
            if (vocabulary.TryGetValue(match.Value, out int special) && match.Value is "<|startoftext|>" or "<|endoftext|>") { ids.Add(special); continue; }
            var symbols = Encoding.UTF8.GetBytes(match.Value).Select(b => byteCharacters[b].ToString()).ToList();
            symbols[^1] += "</w>";
            while (symbols.Count > 1)
            {
                int best = int.MaxValue, position = -1;
                for (int i = 0; i < symbols.Count - 1; i++)
                    if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out int r) && r < best) { best = r; position = i; }
                if (position < 0) break;
                var left = symbols[position]; var right = symbols[position + 1];
                for (int i = 0; i < symbols.Count - 1; i++)
                    if (symbols[i] == left && symbols[i + 1] == right) { symbols[i] += symbols[i + 1]; symbols.RemoveAt(i + 1); }
            }
            foreach (var symbol in symbols)
            {
                if (!vocabulary.TryGetValue(symbol, out int id)) throw new InvalidDataException($"CLIP vocabulary is missing token {symbol}");
                ids.Add(id);
            }
        }
        ids = ids.Take(Length - 1).ToList(); ids.Add(49407);
        var mask = Enumerable.Range(0, Length).Select(i => i < ids.Count ? 1L : 0L).ToArray();
        return (ids.Concat(Enumerable.Repeat(49407L, Length - ids.Count)).ToArray(), mask);
    }
}
