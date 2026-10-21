using Serilog;
using SptArchive.Archiver;
using SptArchive.Archiver.Importers;
using SptArchive.Archiver.Runs;
using SptArchive.Core;
using SptArchive.Core.Caching;
using SptArchive.Core.Data;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.Section));

builder.Services.AddArchiveDatabase(
    builder.Configuration.GetConnectionString("Archive")
        ?? throw new InvalidOperationException("ConnectionStrings:Archive is not configured."));

// The cache is only used to tell the Api that a new dataset is live. Without Redis a local in-memory cache is used,
// which makes the announcement a harmless no-op.
builder.Services.AddArchiveCache(builder.Configuration.GetConnectionString("Redis"));

// Importers run in the order they are registered. Add new ones (hideout, bots, locations, mods, ...) here.
builder.Services.AddSingleton<IImporter, LocaleImporter>();
builder.Services.AddSingleton<IImporter, ItemImporter>();
builder.Services.AddSingleton<IImporter, CustomizationImporter>();
builder.Services.AddSingleton<IImporter, QuestImporter>();
builder.Services.AddSingleton<IImporter, TraderImporter>();

// The run import (TD-29) is not an IImporter: it reads a run directory against its baseline, not a release.
builder.Services.AddSingleton<RunImporter>();

builder.Services.AddHostedService<ArchiveJob>();

try
{
    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
