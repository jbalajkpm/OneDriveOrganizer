using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace OneDriveOrganizer.Services;

public static class PdfEncryption
{
    public static bool IsEncrypted(string path, CancellationToken token)
    {
        using var stream = HashService.Open(path);
        var tail = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        var match = Regex.Match(Encoding.Latin1.GetString(tail), @"startxref\s+(\d+)\s+%%EOF", RegexOptions.RightToLeft | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new InvalidDataException("PDF encryption metadata cannot be located; content was not opened");
        var visited = new HashSet<long>();
        for (int section = 0; section < 128; section++)
        {
            token.ThrowIfCancellationRequested();
            if (offset < 0 || offset >= stream.Length || !visited.Add(offset))
                throw new InvalidDataException("Invalid or cyclic PDF cross-reference metadata; content was not opened");
            stream.Position = offset;
            var bytes = new byte[(int)Math.Min(stream.Length - offset, 1024 * 1024)];
            stream.ReadExactly(bytes);
            using var input = new MemoryInputBytes(bytes);
            var scanner = new CoreTokenScanner(input, false);
            var dictionary = ReadTrailer(scanner, token);
            if (dictionary.Data.TryGetValue("Encrypt", out var encryption))
            {
                if (encryption is NullToken) return false;
                if (encryption is IndirectReferenceToken or DictionaryToken) return true;
                throw new InvalidDataException("Unrecognized PDF encryption metadata; content was not opened");
            }
            if (!dictionary.Data.TryGetValue("Prev", out var previous)) return false;
            if (previous is not NumericToken number || number.HasDecimalPlaces)
                throw new InvalidDataException("Invalid PDF previous cross-reference offset; content was not opened");
            offset = number.Long;
        }
        throw new InvalidDataException("PDF cross-reference history exceeds the inspection limit; content was not opened");
    }

    private static DictionaryToken ReadTrailer(CoreTokenScanner scanner, CancellationToken token)
    {
        if (!scanner.MoveNext()) throw new InvalidDataException("Missing PDF cross-reference metadata");
        if (scanner.CurrentToken is OperatorToken { Data: "xref" })
        {
            while (scanner.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                if (scanner.CurrentToken is OperatorToken { Data: "trailer" })
                {
                    if (scanner.MoveNext() && scanner.CurrentToken is DictionaryToken trailer) return trailer;
                    break;
                }
            }
        }
        else if (scanner.CurrentToken is NumericToken && scanner.MoveNext() && scanner.CurrentToken is NumericToken &&
            scanner.MoveNext() && scanner.CurrentToken is OperatorToken { Data: "obj" } && scanner.MoveNext() &&
            scanner.CurrentToken is DictionaryToken dictionary && dictionary.Data.TryGetValue("Type", out var type) && type is NameToken { Data: "XRef" })
        {
            // Cross-reference stream dictionaries are not encrypted; their stream contents are not needed here.
            return dictionary;
        }
        throw new InvalidDataException("PDF cross-reference metadata is invalid or exceeds 1 MiB; content was not opened");
    }
}
