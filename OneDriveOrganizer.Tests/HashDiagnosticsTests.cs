using System.Globalization;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Models;
using OneDriveOrganizer.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OneDriveOrganizer.Tests;

[CollectionDefinition("Hash diagnostics", DisableParallelization = true)]
public sealed class HashDiagnosticsCollection { }

[Collection("Hash diagnostics")]
public sealed class HashDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneDriveOrganizer-hash-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly ILogger previousLogger = Log.Logger;
    private readonly CaptureSink sink = new();
    private readonly Logger logger;
    private ScanOptions Options => new(root, [], true, 1);
    private IndexService Index => new(new MediaService());

    public HashDiagnosticsTests()
    {
        Directory.CreateDirectory(root);
        logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenFailureLogsPathAndCodeAndPreservesExceptionType(bool partial)
    {
        var path = Path.Combine(root, "missing.txt");
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => HashService.Sha256(path, CancellationToken.None, partial));
        AssertDiagnostic(path, error);
    }

    [WindowsFileLockFact]
    public async Task MidReadFailuresLogPathAndOriginalCodeForBothHashModes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "range-locked.txt");
        File.WriteAllText(path, "contents");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held.Lock(0, held.Length);
        foreach (var partial in new[] { false, true })
        {
            sink.Events.Clear();
            var error = await Assert.ThrowsAsync<IOException>(() => HashService.Sha256(path, CancellationToken.None, partial));
            Assert.Equal(unchecked((int)0x80070021), error.HResult);
            AssertDiagnostic(path, error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationIsNotLoggedAsAnIoFailure(bool partial)
    {
        var path = Path.Combine(root, "cancelled.txt");
        File.WriteAllText(path, "contents");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HashService.Sha256(path, cancellation.Token, partial));
        Assert.Empty(sink.Events);
    }

    [WindowsFileLockFact]
    public async Task IndexSkipsAfterExactlyThreeFailuresAndKeepsOtherFiles()
    {
        var locked = Path.Combine(root, "locked.txt");
        var readable = Path.Combine(root, "readable.txt");
        File.WriteAllText(locked, "busy");
        File.WriteAllText(readable, "available");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var files = await Index.Refresh(Options, CancellationToken.None);
        Assert.Equal(readable, Assert.Single(files).Path);
        Assert.Equal(new[] { 1, 2, 3 }, Attempts());
        var skipped = Assert.Single(sink.Events, e => e.MessageTemplate.Text.StartsWith("Skipping unreadable file", StringComparison.Ordinal));
        Assert.Equal(locked, Assert.IsType<ScalarValue>(skipped.Properties["Path"]).Value);
        using var store = new IndexStore(root);
        Assert.Equal(readable, Assert.Single(store.Load()).Key);
        Assert.True(File.Exists(locked));
    }

    [WindowsFileLockFact]
    public async Task IndexCanSucceedOnTheThirdAttempt()
    {
        var path = Path.Combine(root, "temporary-lock.txt");
        File.WriteAllText(path, "contents");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        sink.OnEmit = entry =>
        {
            if (entry.Properties.TryGetValue("Attempt", out var value) && value is ScalarValue { Value: 2 }) held.Dispose();
        };
        var file = Assert.Single(await Index.Refresh(Options, CancellationToken.None));
        Assert.Equal(path, file.Path);
        Assert.Equal(await HashService.Sha256(path, CancellationToken.None, partial: true), file.PartialHash);
        Assert.Equal(new[] { 1, 2 }, Attempts());
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.StartsWith("Skipping unreadable file", StringComparison.Ordinal));
    }

    [WindowsFileLockFact]
    public async Task CancellationDuringRetryPreservesThePreviousIndex()
    {
        var path = Path.Combine(root, "cached.txt");
        File.WriteAllText(path, "contents");
        await Index.Refresh(Options, CancellationToken.None);
        sink.Events.Clear();
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        sink.OnEmit = entry =>
        {
            if (entry.Properties.ContainsKey("Attempt")) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.Refresh(Options, cancellation.Token));
        Assert.Equal(new[] { 1 }, Attempts());
        using var store = new IndexStore(root);
        Assert.Equal(path, Assert.Single(store.Load()).Key);
        Assert.DoesNotContain(sink.Events, e => e.MessageTemplate.Text.StartsWith("Skipping unreadable file", StringComparison.Ordinal));
    }

    [WindowsFileLockFact]
    public async Task FailedPartialReadsAreRetriedWithoutPersistingIncompleteRecords()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "unreadable.bin");
        File.WriteAllText(path, "synthetic read failure");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held.Lock(0, held.Length);
        Assert.Empty(await Index.Refresh(Options, CancellationToken.None));
        Assert.Equal(new[] { 1, 2, 3 }, Attempts());
        Assert.Equal(3, sink.Events.Count(e => e.MessageTemplate.Text.StartsWith("Cannot read file", StringComparison.Ordinal)));
        using var store = new IndexStore(root);
        Assert.Empty(store.Load());
    }

    private int[] Attempts() => sink.Events.Where(e => e.Properties.ContainsKey("Attempt"))
        .Select(e => Assert.IsType<int>(Assert.IsType<ScalarValue>(e.Properties["Attempt"]).Value)).ToArray();

    private void AssertDiagnostic(string path, IOException error)
    {
        var entry = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, entry.Level);
        Assert.Equal(path, Assert.IsType<ScalarValue>(entry.Properties["Path"]).Value);
        Assert.Equal(error.HResult, Assert.IsType<ScalarValue>(entry.Properties["HResult"]).Value);
        Assert.Equal(error.Message, Assert.IsType<ScalarValue>(entry.Properties["Error"]).Value);
        Assert.Contains($"HRESULT 0x{error.HResult:X8}", entry.RenderMessage(CultureInfo.InvariantCulture));
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public Action<LogEvent>? OnEmit { get; set; }
        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
            OnEmit?.Invoke(logEvent);
        }
    }

    private sealed class WindowsFileLockFactAttribute : FactAttribute
    {
        public WindowsFileLockFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows mandatory byte-range locks.";
        }
    }

    public void Dispose()
    {
        Log.Logger = previousLogger;
        logger.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
}
