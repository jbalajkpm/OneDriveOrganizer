using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;
using OneDriveOrganizer.Services.AI;
using Serilog;

namespace OneDriveOrganizer.Commands;

public static class Cli
{
    public static RootCommand Build(IServiceProvider services, CancellationToken shutdown)
    {
        var root = new RootCommand("OneDriveOrganizer: review first, reversible local file cleanup. All mutations default to dry-run.");
        foreach (var verb in new[] { "index", "dupes", "zips", "photos", "folders", "classify", "report", "search" })
        {
            var command = new Command(verb, verb switch
            {
                "index" => "Incrementally index local files and report sync-conflict candidates",
                "dupes" => "Report size/partial/full SHA-256 duplicates",
                "zips" => "Report redundant ZIPs, nested ZIPs and duplicate archive contents",
                "photos" => "Report visual neighbours; preserve RAW/Live Photo pairs",
                "folders" => "Report duplicate/subset trees (advisory only)",
                "classify" => "Build a content/metadata move plan; never move files",
                "search" => "Search indexed CLIP image embeddings with local text inference",
                _ => "Write a private, standalone HTML summary"
            });
            var path = new Argument<string>("root") { Description = "Locally synced OneDrive folder" };
            var output = new Option<string>("--out") { DefaultValueFactory = _ => verb == "classify" ? "plan.csv" : verb == "report" ? "summary.html" : verb + ".csv", Description = "New output file (never overwrite an existing report)" };
            var exclude = new Option<string[]>("--exclude") { Description = "Excluded path(s), relative to root or absolute", AllowMultipleArgumentsPerToken = true };
            var skip = new Option<bool>("--skip-offline") { DefaultValueFactory = _ => true, Description = "Skip placeholders; set false only to explicitly permit hydration" };
            var parallel = new Option<int>("--parallelism") { DefaultValueFactory = _ => Math.Min(4, Environment.ProcessorCount), Description = "Bounded workers (1..64)" };
            var keep = new Option<string>("--keep") { DefaultValueFactory = _ => "oldest", Description = "oldest | newest | shortest-path | prefer-folder" };
            var prefer = new Option<string?>("--prefer-folder") { Description = "Folder preferred by --keep prefer-folder" };
            var threshold = new Option<int>("--threshold") { DefaultValueFactory = _ => 6, Description = "Maximum dHash distance (0..16)" };
            var drop = new Option<bool>("--drop-jpeg-when-raw") { Description = "Suggest removing JPEG companions when RAW exists; still requires review" };
            var ffmpeg = new Option<string?>("--ffmpeg") { Description = "Optional path to trusted ffmpeg for video first-frame comparisons" };
            var ffprobe = new Option<string?>("--ffprobe") { Description = "Optional path to trusted ffprobe for video durations" };
            var rules = new Option<string?>("--rules") { Description = "JSON rule overrides (same name replaces embedded default)" };
            var learn = new Option<bool>("--learn") { Description = "Write proposed rules; do not activate them" };
            var confidence = new Option<double>("--min-confidence") { DefaultValueFactory = _ => 0.7, Description = "Items below this score go to _Review in the plan and are never auto-moved" };
            var stale = new Option<int>("--stale-years") { DefaultValueFactory = _ => 3, Description = "Archive/installer age for Stale suggestion" };
            var aiMode = new Option<string>("--ai") { DefaultValueFactory = _ => "local", Description = "off | local (default, needs CLIP assets) | cloud (explicit upload opt-in)" };
            var models = new Option<string?>("--clip-model-dir") { Description = "Local CLIP ViT-B/32 model/tokenizer directory; see README" };
            var cosine = new Option<double>("--cosine-threshold") { DefaultValueFactory = _ => 0.92, Description = "Local embedding cosine threshold (0..1)" };
            var sensitiveCloud = new Option<bool>("--allow-sensitive-cloud") { Description = "Explicitly permit upload of locally flagged sensitive content" };
            var query = new Argument<string>("text") { Description = "Quoted semantic image query" };
            var topK = new Option<int>("--top-k") { DefaultValueFactory = _ => 10 };
            bool usesAi = verb is "index" or "photos" or "classify" or "search";
            command.Arguments.Add(path);
            foreach (var option in new Option[] { exclude, skip, parallel }) command.Options.Add(option);
            if (verb != "search") command.Options.Add(output);
            if (usesAi) foreach (var option in new Option[] { aiMode, models }) command.Options.Add(option);
            if (verb == "photos") command.Options.Add(cosine);
            if (verb == "classify") command.Options.Add(sensitiveCloud);
            if (verb == "search") { command.Arguments.Add(query); command.Options.Add(topK); }
            if (verb == "dupes") { command.Options.Add(keep); command.Options.Add(prefer); }
            if (verb == "photos") foreach (var option in new Option[] { threshold, drop, ffmpeg, ffprobe }) command.Options.Add(option);
            if (verb == "classify") foreach (var option in new Option[] { rules, learn, confidence, stale }) command.Options.Add(option);
            command.SetAction(async (ParseResult result, CancellationToken invocation) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(invocation, shutdown);
                return await Guard(async () =>
                {
                    var exclusions = (result.GetValue(exclude) ?? []).Append(LogDirectory).ToList();
                    var mode = usesAi ? result.GetValue(aiMode) switch { "off" => AiMode.Off, "local" => AiMode.Local, "cloud" => AiMode.Cloud, _ => throw new ArgumentException("--ai must be off, local or cloud") } : AiMode.Off;
                    var aiOptions = new AiOptions(mode, usesAi ? result.GetValue(models) : null, verb == "photos" ? result.GetValue(cosine) : 0.92, verb == "classify" && result.GetValue(sensitiveCloud));
                    if (mode != AiMode.Off) exclusions.Add(aiOptions.Models);
                    if (verb == "search" && mode == AiMode.Off) throw new ArgumentException("Semantic search requires local CLIP models; --ai off cannot search embeddings");
                    if (verb == "classify" && result.GetValue(rules) is { } rulePath) exclusions.Add(Path.GetFullPath(rulePath));
                    var options = new ScanOptions(PathPolicy.Normalize(result.GetValue(path)!), exclusions.ToArray(), result.GetValue(skip), result.GetValue(parallel));
                    var outPath = verb == "search" ? "" : result.GetValue(output)!;
                    if (verb != "search" && (File.Exists(outPath) || File.Exists(outPath + ".manifest.json"))) throw new IOException("Output already exists; choose a fresh --out");
                    // A cross-process lock serializes all app work for this root, without waiting or deleting lock files.
                    using var lease = Acquire(options.Root);
                    using var ai = usesAi ? services.GetRequiredService<AiService>().Open(options.Root, aiOptions) : null;
                    var token = linked.Token;
                    switch (verb)
                    {
                        case "index":
                            var files = await services.GetRequiredService<IndexService>().Refresh(options, token);
                            if (ai != null) await ai.IndexImages(files, options, token);
                            await services.GetRequiredService<ReportStore>().Write("index", options, outPath, files.Select(f => DedupService.Row(f) with { Status = f.Conflict.Length > 0 ? "SyncConflictCandidate" : "Indexed", Reason = f.MediaStatus }).ToList(), token);
                            break;
                        case "dupes": await services.GetRequiredService<DedupService>().Dupes(options, outPath, result.GetValue(keep)!, result.GetValue(prefer), token); break;
                        case "zips": await services.GetRequiredService<ArchiveService>().Analyze(options, outPath, token); break;
                        case "photos": await services.GetRequiredService<DedupService>().Photos(options, outPath, result.GetValue(threshold), result.GetValue(drop), result.GetValue(ffmpeg), result.GetValue(ffprobe), token, ai); break;
                        case "folders": await services.GetRequiredService<DedupService>().Folders(options, outPath, token); break;
                        case "classify": await services.GetRequiredService<ClassificationService>().Classify(options, outPath, result.GetValue(rules), result.GetValue(confidence), result.GetValue(learn), result.GetValue(stale), token, ai); break;
                        case "search":
                            var indexed = await services.GetRequiredService<IndexService>().Refresh(options, token);
                            foreach (var hit in await ai!.Search(indexed, options, result.GetValue(query)!, result.GetValue(topK), token)) Console.WriteLine($"{hit.Score:F4}\t{hit.Path}");
                            break;
                        case "report": await services.GetRequiredService<HtmlReportService>().Write(options, outPath, token); break;
                    }
                    if (verb != "search") Log.Information("Written {Report}", Path.GetFullPath(outPath));
                });
            });
            root.Subcommands.Add(command);
        }
        foreach (var verb in new[] { "clean", "organize", "rename", "undo" })
        {
            var command = new Command(verb, verb == "undo" ? "Reverse a durable journal (also dry-run by default)" : "Apply an independently reviewed report; dry-run unless explicitly authorized");
            var input = new Argument<string>(verb == "undo" ? "undo.json" : "report.csv");
            var execute = new Option<bool>("--no-dry-run") { Description = "Explicitly authorize filesystem changes" };
            var reviewed = new Option<bool>("--reviewed") { Description = "Confirm you inspected every proposed action and its keeper/target" };
            var recycle = new Option<bool>("--recycle") { DefaultValueFactory = _ => true, Description = "Windows Recycle Bin plus backup; otherwise reversible _Trash" };
            var copy = new Option<bool>("--copy-instead-of-move") { Description = "Copy and preserve originals" };
            var journal = new Option<string?>("--undo-log") { Description = "New undo journal path (default .odo/undo-GUID.json)" };
            command.Arguments.Add(input); command.Options.Add(execute); command.Options.Add(reviewed);
            if (verb != "undo") command.Options.Add(journal);
            if (verb == "clean") command.Options.Add(recycle);
            if (verb == "organize") command.Options.Add(copy);
            command.SetAction(async (ParseResult result, CancellationToken invocation) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(invocation, shutdown);
                return await Guard(async () =>
                {
                    var file = Path.GetFullPath(result.GetValue(input)!);
                    var rootPath = verb == "undo" ? ReportStore.ReadJson<UndoJournal>(file).Root : ReportStore.ReadJson<ReportManifest>(file + ".manifest.json").Root;
                    using var lease = Acquire(rootPath);
                    var execution = services.GetRequiredService<ExecutionService>();
                    if (verb == "undo") await execution.Undo(file, result.GetValue(execute), result.GetValue(reviewed), linked.Token);
                    else await execution.Apply(file, verb is "organize" or "rename", result.GetValue(execute), result.GetValue(reviewed), verb == "clean" && result.GetValue(recycle), verb == "organize" && result.GetValue(copy), result.GetValue(journal), linked.Token, rename: verb == "rename");
                });
            });
            root.Subcommands.Add(command);
        }
        return root;
    }

    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneDriveOrganizer", "logs");

    private static FileStream Acquire(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var directory = Path.Combine(root, ".odo");
        PathPolicy.AssertNoLinks(directory);
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<int> Guard(Func<Task> action)
    {
        try { await action(); return 0; }
        catch (OperationCanceledException) { Log.Warning("Cancelled; existing undo journal remains recoverable"); return 130; }
        catch (Exception ex) { Log.Error(ex, "Command failed: {Message}", ex.Message); return 1; }
    }
}
