using Serilog;
using SptArchive.Api.Caching;
using SptArchive.Api.Edge;
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
// The pool is capped below the max_connections of the server, so a burst waits for a connection instead
// of asking Postgres for more than it allows (TD-53, ADR-0038).
builder.Services.AddArchiveDatabase(
    builder.Configuration.GetConnectionString("Archive")
        ?? throw new InvalidOperationException("ConnectionStrings:Archive is not configured."),
    readOnly: true,
    maxPoolSize: builder.Configuration.GetValue("Database:MaxPoolSize", 30));

// ---- Cache: Redis when configured, otherwise a per-process memory cache. ----
builder.Services.AddArchiveCache(builder.Configuration.GetConnectionString("Redis"));

builder.Services.Configure<QueryCacheOptions>(builder.Configuration.GetSection(QueryCacheOptions.Section));

// ---- Edge guard: a rate limit per client, compression of every answer, three security headers (ADR-0038). ----
builder.Services.AddArchiveEdge(builder.Configuration);

// ---- Public read-only API, so any origin may call it. It must change with the change that adds
//      authentication: an open origin and credentials never ship together (ADR-0008, ADR-0037, ADR-0038). ----
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .WithMethods("GET", "POST")
    .AllowAnyHeader()));

// ---- GraphQL ----
builder.Services.AddArchiveGraphQL();

var app = builder.Build();

app.UseArchiveSecurityHeaders();
app.UseCors();
app.UseRateLimiter();

// Outside the cache on purpose: the cache stores one uncompressed answer and each client receives it
// compressed to what it asked for (ADR-0038). The other order would store one client's bytes for all.
app.UseResponseCompression();

app.UseMiddleware<QueryCacheMiddleware>();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGraphQL();

app.Run();

/// <summary>The entry point, named so that a test can start the Api in the same process.</summary>
public partial class Program;
