using System.Text.Json;
using OneDriveOrganizer.Infrastructure;

namespace OneDriveOrganizer.Services.AI;

public static class AiSchema
{
    public static readonly string[] Categories = ["Photos", "Documents", "Code", "Archives", "Installers", "Music", "Audio", "Misc"];
    public static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","additionalProperties":false,"required":["category","subcategory","confidence","suggestedFileName","isSensitive","reason"],"properties":{
        "category":{"type":"string","enum":["Photos","Documents","Code","Archives","Installers","Music","Audio","Misc"]},
        "subcategory":{"type":"string"},"confidence":{"type":"number"},"suggestedFileName":{"type":"string"},"isSensitive":{"type":"boolean"},"reason":{"type":"string"}}}
        """).RootElement.Clone();

    public static AiResult Parse(string json)
    {
        if (json.Length > 16000) throw new InvalidDataException("AI response exceeds limit");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string[] fields = ["category", "subcategory", "confidence", "suggestedFileName", "isSensitive", "reason"];
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()))
            throw new InvalidDataException("AI response must contain exactly the six schema fields");
        string Text(string name, int maximum, bool empty = false)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("AI field must be a string: " + name);
            var text = value.GetString()!;
            if (text.Length > maximum || (!empty && string.IsNullOrWhiteSpace(text)) || text.Any(char.IsControl)) throw new InvalidDataException("Invalid AI field: " + name);
            return text;
        }
        string category = Text("category", 30), subcategory = Text("subcategory", 80, true), name = Text("suggestedFileName", 120, true), reason = Text("reason", 2000);
        if (!Categories.Contains(category) || (subcategory.Length > 0 && !SafeName(subcategory)) || (name.Length > 0 && !SafeName(name))) throw new InvalidDataException("AI category or filename is unsafe");
        var confidence = root.GetProperty("confidence"); var sensitive = root.GetProperty("isSensitive");
        if (confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 1 || sensitive.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Invalid AI confidence or sensitivity");
        return new(category, subcategory, score, name, sensitive.GetBoolean(), reason);
    }

    public static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 120 && name == name.Trim() &&
        !name.EndsWith('.') && !name.StartsWith('.') && !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) &&
        !PathPolicy.ProtectedName(name) && !System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string RenameTarget(string source, string suggested)
    {
        if (!SafeName(suggested)) throw new InvalidDataException("Unsafe suggested filename");
        string extension = Path.GetExtension(source);
        string stem = Path.HasExtension(suggested) ? Path.GetFileNameWithoutExtension(suggested) : suggested;
        string name = stem + extension;
        if (!SafeName(name)) throw new InvalidDataException("Unsafe resulting filename");
        return Path.Combine(Path.GetDirectoryName(source)!, name);
    }

    public static bool SensitiveText(string text) => ClassificationService.HasWord(text,
        ["passport", "licence", "license", "pps", "social security", "identity", "medical", "patient", "prescription", "bank statement", "account number", "payslip", "tax"]);
}
