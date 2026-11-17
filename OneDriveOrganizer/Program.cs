using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneDriveOrganizer.Commands;
using OneDriveOrganizer.Infrastructure;
using OneDriveOrganizer.Services;
using Serilog;

Directory.CreateDirectory(Cli.LogDirectory);
Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
    .WriteTo.Console().WriteTo.File(Path.Combine(Cli.LogDirectory, "organizer-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    using var host = Host.CreateDefaultBuilder().UseSerilog().ConfigureServices(services =>
    {
        services.AddSingleton<MediaService>();
        services.AddSingleton<IndexService>();
        services.AddSingleton<ReportStore>();
        services.AddSingleton<DedupService>();
        services.AddSingleton<ArchiveService>();
        services.AddSingleton<RuleEngine>();
        services.AddSingleton<ClassificationService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<HtmlReportService>();
        services.AddSingleton<OneDriveOrganizer.Services.AI.AiService>();
    }).Build();
    return await Cli.Build(host.Services, cancellation.Token).Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Startup failed");
    return 1;
}
finally { await Log.CloseAndFlushAsync(); }
