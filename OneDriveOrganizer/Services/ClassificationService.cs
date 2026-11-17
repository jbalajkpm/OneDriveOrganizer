using System.IO.Compression;
using System.Text;
using System.Xml;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services.AI;
using Serilog;
using UglyToad.PdfPig;

namespace OneDriveOrganizer.Services;

public sealed class ClassificationService(IndexService index, RuleEngine engine, ReportStore reports, MediaService media)
{
    private static readonly string[] CodeExtensions = [".ps1", ".cs", ".py", ".js", ".ts", ".json", ".yaml", ".yml", ".xml", ".sh", ".go", ".rs", ".cpp", ".h", ".toml", ".ini", ".config", ".sln", ".csproj"];
    private static readonly (string Name, string[] Words)[] DocumentTopics =
    [
        ("Identity", ["passport", "licence", "license", "pps"]), ("Finance", ["invoice", "receipt", "payslip", "tax", "bank statement", "p60", "revenue"]),
        ("Legal", ["contract", "agreement", "lease", "will"]), ("Medical", ["medical", "prescription", "hospital", "patient"]),
        ("Education", ["certificate", "transcript", "course"]), ("Travel", ["ticket", "boarding pass", "booking", "visa", "itinerary"])
    ];

    public async Task<List<ReportRow>> Classify(ScanOptions options, string output, string? rulesPath, double minConfidence, bool learn, int staleYears, CancellationToken token, AiSession? ai = null)
    {
        if (!double.IsFinite(minConfidence) || minConfidence is < 0 or > 1 || staleYears < 1) throw new ArgumentException("Confidence must be 0..1 and stale years >= 1");
        var rules = engine.Load(rulesPath);
        var files = await index.Refresh(options, token);
        if (ai != null) await ai.IndexImages(files, options, token);
        var policy = new PathPolicy(options);
        var companionMap = DedupService.PairMap(files);
        var rows = new List<ReportRow>();
        var repositories = new HashSet<string>(PathPolicy.Comparer);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            IndexService.AssertUnchanged(file, policy);
            var repository = policy.Repository(file.Path);
            if (repository != null)
            {
                if (!repositories.Add(repository)) continue;
                var target = Path.Combine(policy.Root, "Code", Path.GetFileName(repository));
                var blocked = PathPolicy.Comparer.Equals(repository, policy.Root) || File.Exists(Path.Combine(repository, ".git")) || policy.Excludes.Any(p => PathPolicy.Within(p, repository));
                if (!blocked)
                {
                    try { _ = await HashService.Fingerprint(repository, true, token); }
                    catch (IOException ex) { blocked = true; Log.Warning("Repository kept intact: {Error}", ex.Message); }
                }
                rows.Add(new ReportRow { Path = repository, IsDirectory = true, Size = files.Where(f => PathPolicy.Within(f.Path, repository)).Sum(f => f.Size),
                    TargetPath = target, DetectedType = "GitRepository", Category = "Code", Confidence = 1,
                    Status = blocked ? "Protected" : PathPolicy.Comparer.Equals(repository, target) ? "AlreadyPlaced" : "Move",
                    Reason = "Repository is one atomic unit; linked worktrees and excluded/offline/protected descendants prevent movement" });
                continue;
            }
            var header = new byte[(int)Math.Min(file.Size, 8192)];
            await using (var stream = HashService.Open(file.Path)) await stream.ReadExactlyAsync(header, token);
            string type = DetectType(header, file.Path);
            string text = Path.GetFileNameWithoutExtension(file.Path);
            bool inspected = false;
            string content = type is "PDF" or "Word" ? ReadDocument(file, type, out inspected) : "";
            if (type == "Text" && ai != null)
            {
                using var reader = new StreamReader(HashService.Open(file.Path), Encoding.UTF8, true);
                var buffer = new char[12000]; int read = await reader.ReadBlockAsync(buffer, token);
                content = new string(buffer, 0, read);
            }
            text += " " + content;
            bool scannedPdf = type == "PDF" && inspected && string.IsNullOrWhiteSpace(content);
            var date = file.Taken ?? (file.CreatedUtc.Year >= 1980 ? file.CreatedUtc : MediaService.FilenameDate(file.Path) ?? file.LastWriteUtc);
            var project = Path.GetFileName(Path.GetDirectoryName(file.Path))!;
            string category, folder, reason, artist = "Unknown", album = "Unknown";
            double confidence;
            bool sensitive = HasWord(text, DocumentTopics[0].Words) || (ai != null && AiSchema.SensitiveText(text));
            if (type is "Image" or "Video" || MediaService.Raw.Contains(file.Extension) || file.Extension is ".heic" or ".heif" || companionMap[file.Path].Count > 0)
            {
                if (type == "Image" && file.PerceptualHash == null && !MediaService.Images.Contains(file.Extension))
                {
                    var originalExtension = file.Extension;
                    file.Extension = ".jpg";
                    await media.Read(file, token);
                    file.Extension = originalExtension;
                    date = file.Taken ?? date;
                }
                var companions = companionMap[file.Path];
                var anchor = companions.Append(file).OrderBy(f => f.Extension == ".mov" ? 1 : 0).ThenByDescending(f => f.Taken.HasValue).ThenBy(f => f.Path, PathPolicy.Comparer).First();
                if (companions.Count > 0) date = anchor.Taken ?? (anchor.CreatedUtc.Year >= 1980 ? anchor.CreatedUtc : MediaService.FilenameDate(anchor.Path) ?? anchor.LastWriteUtc);
                bool hasMetadataDate = companions.Count > 0 ? anchor.Taken.HasValue : file.Taken.HasValue;
                string sub = "";
                if (companions.Count == 0)
                {
                    if (type == "Video") sub = "Videos";
                    else if (file.Path.Contains("screenshot", StringComparison.OrdinalIgnoreCase)) sub = "Screenshots";
                    else if (file.Path.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)) sub = "WhatsApp";
                    else if (file.Width > 0 && file.Height > 0 && (double)file.Height / file.Width is > 1.25 and < 1.6 && file.ColorVariance < 100) sub = "Scans";
                    else if (!file.HasExif && Math.Max(file.Width, file.Height) is > 0 and < 1000 && file.Path.Contains("downloads", StringComparison.OrdinalIgnoreCase)) sub = "Memes";
                }
                category = string.IsNullOrEmpty(sub) ? "Photos" : "Photos/" + sub;
                folder = Path.Combine("Photos", date.ToString("yyyy"), date.ToString("yyyy-MM"), sub);
                confidence = hasMetadataDate ? 0.96 : sub is "Scans" or "Memes" ? 0.65 : 0.8;
                reason = $"Media content/metadata; date={(hasMetadataDate ? "EXIF/QuickTime" : "creation then filename")}; camera={file.Camera}; GPS={(file.Gps.Length > 0 ? "present; no local geocoder configured" : "none")}; companion={(companions.Count > 0 ? "kept together" : "none")}";
            }
            else if (type is "PDF" or "Word" or "Excel" or "Presentations" or "Text")
            {
                if (type == "Text" && CodeExtensions.Contains(file.Extension))
                {
                    category = "Code"; folder = Path.Combine("Code", PathPolicy.Component(project)); confidence = 0.85; reason = "Text content and technical extension";
                }
                else
                {
                    var topic = DocumentTopics.FirstOrDefault(t => HasWord(text, t.Words)).Name;
                    category = "Documents/" + type + (topic == null ? "" : "/" + topic);
                    folder = category; confidence = topic == null ? 0.9 : 0.85;
                    reason = "Content signature; filename and first-page/leading document text";
                }
            }
            else if (type is "Archive" or "Installer")
            {
                category = type == "Archive" ? "Archives" : "Installers";
                folder = category + (file.LastWriteUtc < DateTime.UtcNow.AddYears(-staleYears) ? "/Stale" : "");
                confidence = 0.9; reason = "Content signature; age-based stale suggestion";
            }
            else if (type == "Audio")
            {
                try
                {
                    using var audio = TagLib.File.Create(file.Path);
                    artist = audio.Tag.FirstPerformer ?? "Unknown"; album = audio.Tag.Album ?? "Unknown";
                }
                catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
                { Log.Warning("Audio tags unavailable {Path}: {Error}", file.Path, ex.Message); }
                bool recording = file.Path.Contains("record", StringComparison.OrdinalIgnoreCase) || file.Path.Contains("voice", StringComparison.OrdinalIgnoreCase) || file.Path.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);
                category = recording ? "Audio/Recordings" : "Music";
                folder = recording ? category : Path.Combine("Music", PathPolicy.Component(artist), PathPolicy.Component(album));
                confidence = 0.85; reason = "Audio signature and ID3/container tags";
            }
            else
            {
                category = "Misc"; folder = Path.Combine("Misc", PathPolicy.Component(file.Extension.TrimStart('.'))); confidence = 0.4;
                reason = "No reliable content signature; extension is only a fallback";
            }
            var rule = rules.FirstOrDefault(r => RuleEngine.Matches(r, file, header, text));
            // Companion placement outranks custom rules so RAW/Live Photo pairs are not separated.
            if (rule != null && companionMap[file.Path].Count == 0)
            {
                category = rule.Name; folder = RuleEngine.Render(rule.Target, file, date, project, artist, album);
                confidence = rule.Confidence; sensitive |= rule.Sensitive; reason = "Rule: " + rule.Name + "; " + reason;
            }
            string suggestedName = "", aiProvider = "";
            if (ai != null && file.MediaStatus == "Decoded")
            {
                var label = await ai.Label(file.Path, rules, token);
                sensitive |= label.Sensitive;
                aiProvider = "LocalCLIP";
                if (rule == null && companionMap[file.Path].Count == 0)
                {
                    category = label.Rule.AiCategory ?? label.Rule.Name; folder = RuleEngine.Render(label.Rule.Target, file, date, project);
                    confidence = label.Confidence; reason = "CLIP zero-shot label: " + label.Rule.AiLabel + "; softmax score, not calibrated probability; " + reason;
                }
            }
            if (ai != null && scannedPdf) { confidence = Math.Min(confidence, 0.4); reason += "; no first-page text (scan candidate)"; }
            if (ai != null && companionMap[file.Path].Count == 0 && ((type == "Image" && file.MediaStatus == "Decoded") || scannedPdf || !string.IsNullOrWhiteSpace(content)))
            {
                IndexService.AssertUnchanged(file, policy);
                file.FullHash = await HashService.Sha256(file.Path, token);
                var enriched = await ai.Enrich(new(file.Path, file.FullHash, type, text, sensitive, scannedPdf), confidence, minConfidence, token);
                if (enriched != null)
                {
                    category = enriched.Category + (enriched.Subcategory.Length == 0 ? "" : "/" + enriched.Subcategory);
                    folder = enriched.Category == "Photos" ? Path.Combine("Photos", date.ToString("yyyy"), date.ToString("yyyy-MM"), enriched.Subcategory) : category;
                    confidence = enriched.Confidence; sensitive |= enriched.IsSensitive; suggestedName = enriched.SuggestedFileName;
                    reason += "; Azure AI: " + enriched.Reason; aiProvider = "AzureAI";
                }
            }
            var targetFolder = Path.GetFullPath(Path.Combine(policy.Root, folder.Replace('/', Path.DirectorySeparatorChar)));
            policy.AssertAllowed(targetFolder);
            string status = confidence < minConfidence ? "Review" : "Move";
            if (status == "Review") targetFolder = Path.Combine(policy.Root, "_Review", PathPolicy.Component(category));
            var targetPath = Path.Combine(targetFolder, Path.GetFileName(file.Path));
            var meaningful = Path.GetRelativePath(policy.Root, Path.GetDirectoryName(file.Path)!).Split(Path.DirectorySeparatorChar);
            if (status != "Review" && (PathPolicy.Comparer.Equals(targetPath, file.Path) || meaningful.Any(p => p.Equals(category.Split('/').Last(), StringComparison.OrdinalIgnoreCase))))
            { status = "AlreadyPlaced"; targetPath = file.Path; }
            rows.Add(DedupService.Row(file) with { DetectedType = type, Category = category, Confidence = confidence, TargetPath = targetPath,
                Reason = reason, Status = status, Sensitive = sensitive, SuggestedFileName = suggestedName, AiProvider = aiProvider });
            IndexService.AssertUnchanged(file, policy);
        }
        IndexService.Persist(options.Root, files);
        await reports.Write("classify", options, output, rows, token);
        if (learn)
        {
            var suggestions = files.GroupBy(f => f.Extension).Where(g => g.Count() >= 3).Select(g =>
            {
                var dominant = g.GroupBy(f => Path.GetRelativePath(policy.Root, Path.GetDirectoryName(f.Path)!)).OrderByDescending(x => x.Count()).First();
                return (Extension: g.Key, Folder: dominant.Key, Ratio: (double)dominant.Count() / g.Count());
            }).Where(x => x.Ratio >= 0.7 && x.Folder != ".").Select(x => new Rule { Name = "Learned-" + x.Extension, Extensions = [x.Extension], Target = x.Folder.Replace('\\', '/'), Priority = 100, Confidence = x.Ratio }).ToList();
            var learned = Path.GetFullPath(output + ".suggested-rules.json");
            ReportStore.WriteJson(learned, suggestions, false);
            using var store = new IndexStore(policy.Root); store.Register(learned);
            Log.Information("Proposed {Count} rules in {Path}; not automatically enabled", suggestions.Count, learned);
        }
        return rows;
    }

    public static bool HasWord(string text, string[] words) => words.Any(word => System.Text.RegularExpressions.Regex.IsMatch(text,
        @"(?<!\p{L})" + System.Text.RegularExpressions.Regex.Escape(word) + @"(?!\p{L})", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));

    public static string DetectType(byte[] h, string path)
    {
        bool Starts(string hex) => h.AsSpan().StartsWith(Convert.FromHexString(hex));
        if (Starts("25504446")) return "PDF";
        if (Starts("7B5C727466")) return "Word";
        if (Starts("FFD8FF") || Starts("89504E470D0A1A0A") || Starts("47494638") || Starts("424D") || Starts("49492A00") || Starts("4D4D002A")) return "Image";
        if (h.Length >= 12 && Encoding.ASCII.GetString(h, 0, 4) == "RIFF") return Encoding.ASCII.GetString(h, 8, 4) switch { "WEBP" => "Image", "WAVE" => "Audio", "AVI " => "Video", _ => "Unknown" };
        if (h.Length >= 12 && Encoding.ASCII.GetString(h, 4, 4) == "ftyp") return Encoding.ASCII.GetString(h, 8, 4) switch { "heic" or "heix" or "hevc" or "mif1" or "avif" => "Image", "M4A " => "Audio", _ => "Video" };
        if (Starts("1A45DFA3")) return "Video";
        if (Starts("494433") || Starts("664C6143") || Starts("4F676753") || (h.Length > 1 && h[0] == 255 && (h[1] & 0xE0) == 0xE0)) return "Audio";
        if (Starts("4D5A")) return "Installer";
        if (Starts("377ABCAF271C") || Starts("526172211A07") || Starts("1F8B")) return "Archive";
        if (Starts("504B0304") || Starts("504B0506"))
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("word/document.xml") != null) return "Word";
            if (zip.GetEntry("xl/workbook.xml") != null) return "Excel";
            if (zip.GetEntry("ppt/presentation.xml") != null) return "Presentations";
            if (zip.GetEntry("AndroidManifest.xml") != null) return "Installer";
            return "Archive";
        }
        if (Starts("D0CF11E0A1B11AE1")) return Path.GetExtension(path).ToLowerInvariant() switch { ".doc" => "Word", ".xls" => "Excel", ".ppt" => "Presentations", ".msi" => "Installer", _ => "Unknown" };
        if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (stream.Length >= 32774)
            {
                stream.Position = 32769;
                var signature = new byte[5]; stream.ReadExactly(signature);
                if (Encoding.ASCII.GetString(signature) == "CD001") return "Archive";
            }
        }
        if (Path.GetExtension(path).Equals(".dmg", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (stream.Length >= 512)
            {
                stream.Position = stream.Length - 512;
                var signature = new byte[4]; stream.ReadExactly(signature);
                if (Encoding.ASCII.GetString(signature) == "koly") return "Installer";
            }
        }
        try
        {
            var encoding = h.AsSpan().StartsWith(new byte[] { 255, 254 }) ? (Encoding)new UnicodeEncoding(false, true, true) :
                h.AsSpan().StartsWith(new byte[] { 254, 255 }) ? new UnicodeEncoding(true, true, true) : new UTF8Encoding(false, true);
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(h.Length)];
            int count = decoder.GetChars(h, 0, h.Length, chars, 0, flush: false);
            if (chars.Take(count).All(c => !char.IsControl(c) || c is '\t' or '\r' or '\n')) return "Text";
        }
        catch (DecoderFallbackException) { return "Unknown"; }
        return "Unknown";
    }

    private static string ReadDocument(FileRecord file, string type, out bool inspected)
    {
        inspected = false;
        if (file.Size > 50 * 1024 * 1024) { Log.Warning("Text inspection limit (50 MiB): {Path}", file.Path); return ""; }
        try
        {
            if (type == "PDF") { using var pdf = PdfDocument.Open(file.Path); if (pdf.NumberOfPages == 0) return ""; var first = pdf.GetPage(1).Text; inspected = true; return first[..Math.Min(first.Length, 12000)]; }
            using var stream = File.OpenRead(file.Path);
            if (stream.ReadByte() != 'P' || stream.ReadByte() != 'K') return "";
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null || entry.Length > 10 * 1024 * 1024) return "";
            using var content = entry.Open();
            using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
            var text = new StringBuilder();
            while (text.Length < 12000 && reader.Read()) if (reader.NodeType == XmlNodeType.Text) text.Append(reader.Value).Append(' ');
            return text.ToString();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        { Log.Warning("Document text unavailable {Path}: {Error}", file.Path, ex.Message); return ""; }
    }
}
