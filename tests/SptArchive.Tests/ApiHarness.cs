using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HotChocolate.Execution.Configuration;
using SptArchive.Api.GraphQL;
using SptArchive.Core.Data;

namespace SptArchive.Tests;

/// <summary>
/// Runs one GraphQL query against the schema that the Api serves, over one test transaction
/// (see <see cref="ArchiveDatabase.BeginAsync"/>). The answer is the wire format of the Api: the
/// same formatter that answers an HTTP request.
/// </summary>
public static class ApiHarness
{
    public static async Task<string> RunAsync(TestScope scope, string query, List<string>? commands = null) =>
        await RunAsync(scope, new OperationRequestBuilder().SetDocument(query).Build(), commands);

    /// <summary>Runs a request that carries variables. <paramref name="commands"/> collects the SQL that the query ran.</summary>
    public static async Task<string> RunAsync(TestScope scope, IOperationRequest request, List<string>? commands = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ArchiveDbContext>>(new ScopeFactory(scope, commands));

        // A failure that says only "Unexpected Execution Error" names no cause. The tests see the exception;
        // a client of the Api does not.
        services.AddArchiveGraphQL().ModifyRequestOptions(o => o.IncludeExceptionDetails = true);

        await using var provider = services.BuildServiceProvider();
        var executor = await provider.GetRequiredService<IRequestExecutorProvider>()
            .GetExecutorAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(request, TestContext.Current.CancellationToken);

        using var stream = new MemoryStream();
        var writer = System.IO.Pipelines.PipeWriter.Create(stream);
        await new HotChocolate.Transport.Formatters.JsonResultFormatter(indented: false)
            .FormatAsync(result, writer, default, TestContext.Current.CancellationToken);
        await writer.CompleteAsync();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static async Task<JsonElement> JsonAsync(TestScope scope, string query, List<string>? commands = null)
    {
        var result = await RunAsync(scope, query, commands);
        return JsonDocument.Parse(result).RootElement.Clone();
    }

    /// <summary>The nodes of a paged field, as written by the Api.</summary>
    public static JsonElement Nodes(JsonElement response, string field) =>
        response.GetProperty("data").GetProperty(field).GetProperty("nodes");

    public static (string Code, string Message) Error(JsonElement response)
    {
        var error = response.GetProperty("errors")[0];
        return (error.GetProperty("extensions").GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    private sealed class ScopeFactory(TestScope scope, List<string>? commands) : IDbContextFactory<ArchiveDbContext>
    {
        public ArchiveDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<ArchiveDbContext>()
                .UseNpgsql(scope.Connection)
                .UseSnakeCaseNamingConvention();

            if (commands is not null)
            {
                builder.LogTo(
                    (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted,
                    data => commands.Add(((Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData)data).Command.CommandText));
            }

            var options = builder.Options;

            var db = new ArchiveDbContext(options);
            db.Database.UseTransaction(scope.Transaction);
            return db;
        }
    }
}
