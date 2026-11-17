using System.Text.Json;
using System.Text.RegularExpressions;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Infrastructure;

namespace OneDriveOrganizer.Services;

public sealed class RuleEngine
{
    public List<Rule> Load(string? path)
    {
        using var defaults = typeof(RuleEngine).Assembly.GetManifestResourceStream("OneDriveOrganizer.rules.json") ?? throw new InvalidDataException("Embedded rules missing");
        var rules = JsonSerializer.Deserialize<List<Rule>>(defaults, ReportStore.JsonOptions) ?? throw new InvalidDataException("Invalid defaults");
        if (path != null)
        {
            var custom = ReportStore.ReadJson<List<Rule>>(path);
            rules.RemoveAll(r => custom.Any(c => c.Name == r.Name));
            rules.AddRange(custom);
        }
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Target) || !double.IsFinite(rule.Confidence) || rule.Confidence is < 0 or > 1)
                throw new InvalidDataException("Rules require a name, target, and confidence within 0..1");
            if (rule.FilenameRegex != null) _ = new Regex(rule.FilenameRegex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (var magic in rule.MagicBytes) _ = Convert.FromHexString(magic);
        }
        return rules.OrderByDescending(r => r.Priority).ToList();
    }

    public static bool Matches(Rule rule, FileRecord file, byte[] header, string text)
    {
        return rule.AiLabel == null && (rule.Extensions.Length == 0 || rule.Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)) &&
            (rule.MagicBytes.Length == 0 || rule.MagicBytes.Any(m => header.AsSpan().StartsWith(Convert.FromHexString(m)))) &&
            (rule.FilenameRegex == null || Regex.IsMatch(Path.GetFileName(file.Path), rule.FilenameRegex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) &&
            (rule.ExifPresence == null || rule.ExifPresence == file.HasExif) &&
            (rule.TextKeywords.Length == 0 || rule.TextKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) &&
            (rule.MinSize == null || file.Size >= rule.MinSize) && (rule.MaxSize == null || file.Size <= rule.MaxSize) &&
            (rule.MinWidth == null || file.Width >= rule.MinWidth) && (rule.MaxWidth == null || file.Width <= rule.MaxWidth) &&
            (rule.MinHeight == null || file.Height >= rule.MinHeight) && (rule.MaxHeight == null || file.Height <= rule.MaxHeight);
    }

    public static string Render(string template, FileRecord file, DateTime date, string project, string artist = "Unknown", string album = "Unknown")
    {
        var result = template.Replace("{yyyy-MM}", date.ToString("yyyy-MM")).Replace("{yyyy}", date.ToString("yyyy"))
            .Replace("{Extension}", PathPolicy.Component(file.Extension.TrimStart('.'))).Replace("{ProjectName}", PathPolicy.Component(project))
            .Replace("{Artist}", PathPolicy.Component(artist)).Replace("{Album}", PathPolicy.Component(album));
        if (result.Contains('{') || result.Contains('}')) throw new InvalidDataException($"Unsupported target placeholder: {template}");
        return result.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
