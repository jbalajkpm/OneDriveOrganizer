namespace OneDriveOrganizer.Models;

public sealed record ScanOptions(string Root, string[] Excludes, bool SkipOffline = true, int Parallelism = 4);

public sealed class FileRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public int Attributes { get; set; }
    public string Extension { get; set; } = "";
    public string PartialHash { get; set; } = "";
    public string? FullHash { get; set; }
    public uint? Crc32 { get; set; }
    public ulong? PerceptualHash { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasExif { get; set; }
    public DateTime? Taken { get; set; }
    public string Camera { get; set; } = "";
    public string Gps { get; set; } = "";
    public double? Duration { get; set; }
    public double ColorVariance { get; set; }
    public string MediaStatus { get; set; } = "";
    public int MediaAnalysisVersion { get; set; }
    public string Conflict { get; set; } = "";
}

public sealed record ReportRow
{
    public string GroupId { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Path { get; set; } = "";
    public bool Keep { get; set; } = true;
    public string KeptPath { get; set; } = "";
    public string Status { get; set; } = "";
    public string DetectedType { get; set; } = "";
    public string Category { get; set; } = "";
    public double Confidence { get; set; }
    public string TargetPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Sensitive { get; set; }
    public string Conflict { get; set; } = "";
    public bool IsDirectory { get; set; }
    [CsvHelper.Configuration.Attributes.Optional]
    public string SuggestedFileName { get; set; } = "";
    [CsvHelper.Configuration.Attributes.Optional]
    public string AiProvider { get; set; } = "";
}

public sealed class ReportManifest
{
    public int Version { get; set; } = 1;
    public int MediaAnalysisVersion { get; set; }
    public string Kind { get; set; } = "";
    public string Root { get; set; } = "";
    public string[] Excludes { get; set; } = [];
    public bool SkipOffline { get; set; } = true;
    public List<ReportRow> Rows { get; set; } = [];
    public Dictionary<string, string> Proofs { get; set; } = new();
}

public sealed class UndoJournal
{
    public int Version { get; set; } = 1;
    public string Root { get; set; } = "";
    public string[] Excludes { get; set; } = [];
    public List<UndoEntry> Entries { get; set; } = [];
}

public sealed class UndoEntry
{
    public string Original { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Action { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool IsDirectory { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string State { get; set; } = "Prepared";
    public string? Staging { get; set; }
    public string? UndoDestination { get; set; }
}

public sealed class Rule
{
    public string Name { get; set; } = "";
    public string? AiLabel { get; set; }
    public string? AiCategory { get; set; }
    public int Priority { get; set; }
    public string[] Extensions { get; set; } = [];
    public string[] MagicBytes { get; set; } = [];
    public string? FilenameRegex { get; set; }
    public bool? ExifPresence { get; set; }
    public string[] TextKeywords { get; set; } = [];
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public int? MinWidth { get; set; }
    public int? MaxWidth { get; set; }
    public int? MinHeight { get; set; }
    public int? MaxHeight { get; set; }
    public string Target { get; set; } = "";
    public double Confidence { get; set; } = 0.9;
    public bool Sensitive { get; set; }
}
