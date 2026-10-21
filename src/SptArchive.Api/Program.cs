using Serilog;
using SptArchive.Api.Caching;
using SptArchive.Api.GraphQL;
using SptArchive.Core;
using SptArchive.Core.Caching;
using SptArchive.Core.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((_, logger) => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console());

// ---- Database: read-only. Point ConnectionStrings:Archive at a Postgres role that only has SELECT. ----
builder.Services.AddArchiveDatabase(
    builder.Configuration.GetConnectionString("Archive")
        ?? throw new InvalidOperationException("ConnectionStrings:Archive is not configured."),
    readOnly: true);

// ---- Cache: Redis when configured, otherwise a per-process memory cache. ----
builder.Services.AddArchiveCache(builder.Configuration.GetConnectionString("Redis"));

builder.Services.Configure<QueryCacheOptions>(builder.Configuration.GetSection(QueryCacheOptions.Section));

// ---- Public read-only API, so any origin may call it. ----
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .WithMethods("GET", "POST")
    .AllowAnyHeader()));

// ---- GraphQL ----
builder.Services.AddArchiveGraphQL();

var app = builder.Build();

app.UseCors();
app.UseMiddleware<QueryCacheMiddleware>();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGraphQL();

app.Run();
