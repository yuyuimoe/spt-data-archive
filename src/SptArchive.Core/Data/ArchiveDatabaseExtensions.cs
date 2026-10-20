using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
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
    public static IServiceCollection AddArchiveDatabase(
        this IServiceCollection services,
        string connectionString,
        bool readOnly = false)
    {
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
