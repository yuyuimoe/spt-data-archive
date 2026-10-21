using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace SptArchive.Core.Data;

public static class ArchiveDatabaseExtensions
{
    /// <summary>
    /// Registers a pooled <see cref="IDbContextFactory{TContext}"/> for the archive database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Npgsql connection string.</param>
    /// <param name="readOnly">
    /// When true, queries are no-tracking by default. The Api sets this, and also connects with a
    /// Postgres role that only has SELECT, so the guarantee does not depend on application code.
    /// </param>
    /// <param name="maxPoolSize">
    /// The most connections the pool opens. The Api sets it below the max_connections of the server, so
    /// that a burst waits for a connection instead of failing with 53300 (TD-53, ADR-0038). Null keeps
    /// the Npgsql default of 100.
    /// </param>
    public static IServiceCollection AddArchiveDatabase(
        this IServiceCollection services,
        string connectionString,
        bool readOnly = false,
        int? maxPoolSize = null)
    {
        if (maxPoolSize is > 0)
        {
            connectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = maxPoolSize.Value,
            }.ConnectionString;
        }

        services.AddPooledDbContextFactory<ArchiveDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseSnakeCaseNamingConvention();

            if (readOnly)
            {
                options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            }
        });

        return services;
    }
}

/// <summary>
/// Lets "dotnet ef" create migrations without starting an application.
/// The connection string is only used when applying migrations, never when scaffolding them.
/// </summary>
public sealed class ArchiveDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ARCHIVE_CONNECTION")
            ?? "Host=localhost;Database=archive;Username=archive_owner;Password=change-me";

        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ArchiveDbContext(options);
    }
}
