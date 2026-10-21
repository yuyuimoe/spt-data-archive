using HotChocolate;
using HotChocolate.Execution;
using Npgsql;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// Answers a JSON value that PostgreSQL refuses with <see cref="JsonFilterErrors.ValueRejectedCode"/> and not with
/// "Unexpected Execution Error" (ADR-0040, TD-55). The Api checks the shape of a value before it runs the query, but
/// only the database knows two limits of the jsonb type: a NUL character in a string and a number outside the numeric
/// range. The message never repeats the database text, which can name the value.
/// </summary>
public sealed class JsonValueErrorFilter : IErrorFilter
{
    // 22P02 invalid text representation, 22P05 untranslatable character (a NUL character), 22003 numeric value out of range.
    private static readonly HashSet<string> RejectedStates = ["22P02", "22P05", "22003"];

    public IError OnError(IError error)
    {
        for (var exception = error.Exception; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException { SqlState: { } state } && RejectedStates.Contains(state))
            {
                return ErrorBuilder.New()
                    .SetMessage("The database refused a JSON value of the filter. A string holds no NUL character, and a number stays inside the range of PostgreSQL's numeric type.")
                    .SetCode(JsonFilterErrors.ValueRejectedCode)
                    .SetPath(error.Path)
                    .Build();
            }
        }

        return error;
    }
}
