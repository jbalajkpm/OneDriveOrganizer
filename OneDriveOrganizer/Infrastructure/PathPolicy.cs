using System.Text.RegularExpressions;
using OneDriveOrganizer.Models;
using Serilog;

namespace OneDriveOrganizer.Infrastructure;

public sealed class PathPolicy
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public string Root { get; }
    public string[] Excludes { get; }
    public bool SkipOffline { get; }
    public int SkippedOffline { get; private set; }

    public PathPolicy(ScanOptions options)
    {
        Root = Normalize(options.Root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        Excludes = options.Excludes.Select(p => Normalize(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(Root, p))).ToArray();
        SkipOffline = options.SkipOffline;
        AssertNoLinks(Root);
        if (SkipOffline && IsOffline(File.GetAttributes(Root))) throw new IOException("Root is an offline placeholder; make it locally available first");
    }

    public static string Normalize(string path)
    {
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    public static bool Within(string path, string folder)
    {
        var normalized = Normalize(folder);
        var prefix = System.IO.Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + System.IO.Path.DirectorySeparatorChar;
        return Comparer.Equals(Normalize(path), normalized) || Normalize(path).StartsWith(prefix, Comparison);
    }

    public static bool IsOffline(FileAttributes attributes) => ((int)attributes & ((int)FileAttributes.Offline | 0x400000)) != 0;
    public static bool IsLink(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;
    public static bool IsLink(string path) => (Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).LinkTarget != null;
    public static bool ProtectedName(string path) => new[] { "desktop.ini", "Thumbs.db", ".DS_Store", ".849C9593-D756-4E56-8D6E-42412F2A707B" }.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
        System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public bool Excluded(string path) => !Within(path, Root) || Excludes.Any(p => Within(path, p)) ||
        System.IO.Path.GetRelativePath(Root, path).Split(System.IO.Path.DirectorySeparatorChar).Any(p => p is ".odo" or "_Trash" or ".git") || ProtectedName(path);

    public void AssertAllowed(string path, bool allowInternal = false)
    {
        if (!Within(path, Root) || Comparer.Equals(Normalize(path), Root)) throw new IOException($"Path is outside the root or is the root: {path}");
        if (Excludes.Any(p => Within(path, p)) || ProtectedName(path) || (!allowInternal && Excluded(path))) throw new IOException($"Protected or excluded path: {path}");
        AssertNoLinks(path);
        if ((File.Exists(path) || Directory.Exists(path)) && SkipOffline && IsOffline(File.GetAttributes(path)))
            throw new IOException($"Offline placeholder cannot be read: {path}");
    }

    public static void AssertNoLinks(string path)
    {
        for (string? p = Normalize(path); p != null; p = System.IO.Path.GetDirectoryName(p))
        {
            if ((File.Exists(p) || Directory.Exists(p)) && IsLink(p))
                throw new IOException($"Reparse points/symlinks are not traversed: {p}");
        }
    }

    public IEnumerable<string> Enumerate(CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(Root);
        while (stack.TryPop(out var dir))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(dir).Order(Comparer))
            {
                token.ThrowIfCancellationRequested();
                if (Excluded(path)) continue;
                var attrs = File.GetAttributes(path);
                if (SkipOffline && IsOffline(attrs)) { SkippedOffline++; continue; }
                if (IsLink(path)) { Log.Warning("Skipping link/junction {Path}", path); continue; }
                if ((attrs & FileAttributes.Directory) != 0) stack.Push(path);
                else yield return Normalize(path);
            }
        }
    }

    public string? Repository(string path)
    {
        for (var dir = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path); dir != null && Within(dir, Root); dir = System.IO.Path.GetDirectoryName(dir))
            if (Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git"))) return dir;
        return null;
    }

    public static string Conflict(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        return Regex.IsMatch(name, @"-(?:DESKTOP|LAPTOP)-[A-Z0-9]+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "DeviceConflict" :
            Regex.IsMatch(name, @"(?: \(\d+\)| - Copy(?: \(\d+\))?)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "PossibleConflict" : "";
    }

    public static string Component(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var clean = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return string.IsNullOrEmpty(clean) ? "Unknown" : clean;
    }
}
