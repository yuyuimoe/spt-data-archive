using Serilog;
using SptArchive.Archiver;
using SptArchive.Archiver.Importers;
using SptArchive.Core;
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
var redis = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redis;
        o.InstanceName = CacheKeys.RedisInstanceName;
    });
}

// Importers run in the order they are registered. Add new ones (hideout, bots, locations, mods, ...) here.
builder.Services.AddSingleton<IImporter, LocaleImporter>();
builder.Services.AddSingleton<IImporter, ItemImporter>();
builder.Services.AddSingleton<IImporter, QuestImporter>();
builder.Services.AddSingleton<IImporter, TraderImporter>();

builder.Services.AddHostedService<ArchiveJob>();

try
{
    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
