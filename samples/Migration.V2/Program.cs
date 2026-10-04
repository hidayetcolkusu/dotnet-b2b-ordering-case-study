using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// Migration.V2 - the "transitional application" in the compatibility experiment.
//
// It writes the same reference to both columns and, when reading, prefers the new column and
// falls back to the old one. That is what lets V1 and V2 run against schema S2 at the same time.
//
// Usage:
//   Migration.V2 write --order <guid> --company <guid> --user <guid> [--reference <text>]
//   Migration.V2 read  --order <guid>
//
// The connection string is read from MIGRATION_DB_CONNECTION and is never echoed to stdout.

const string Version = "V2";

try
{
    var options = CommandLine.Parse(args);
    var connectionString = Environment.GetEnvironmentVariable("MIGRATION_DB_CONNECTION");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Fail("MIGRATION_DB_CONNECTION is not set.");
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    return options.Operation switch
    {
        "write" => await WriteAsync(connection, options),
        "read" => await ReadAsync(connection, options),
        _ => Fail($"Unknown operation '{options.Operation}'. Use 'write' or 'read'."),
    };
}
catch (Exception exception)
{
    return Fail(exception.Message);
}

async Task<int> WriteAsync(SqlConnection connection, CommandLine options)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO [Orders]
            ([Id], [CompanyId], [CreatedByUserId], [TotalAmount], [CreatedAtUtc],
             [CustomerReference], [ExternalReference])
        VALUES (@id, @companyId, @userId, @total, SYSUTCDATETIME(), @reference, @reference);
        """;

    command.Parameters.AddWithValue("@id", options.OrderId);
    command.Parameters.AddWithValue("@companyId", options.CompanyId);
    command.Parameters.AddWithValue("@userId", options.UserId);
    command.Parameters.AddWithValue("@total", 0m);
    command.Parameters.AddWithValue("@reference", (object?)options.Reference ?? DBNull.Value);

    await command.ExecuteNonQueryAsync();

    return Report(new Result(Version, "write", options.OrderId, options.Reference, "both"));
}

async Task<int> ReadAsync(SqlConnection connection, CommandLine options)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT [ExternalReference], [CustomerReference] FROM [Orders] WHERE [Id] = @id;";
    command.Parameters.AddWithValue("@id", options.OrderId);

    await using var reader = await command.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        return Fail($"No order with id {options.OrderId}.");
    }

    // Prefer the new column; fall back to the old one for rows a V1 instance wrote.
    if (!reader.IsDBNull(0))
    {
        return Report(new Result(Version, "read", options.OrderId, reader.GetString(0), "ExternalReference"));
    }

    return reader.IsDBNull(1)
        ? Report(new Result(Version, "read", options.OrderId, null, "none"))
        : Report(new Result(Version, "read", options.OrderId, reader.GetString(1), "CustomerReference"));
}

static int Report(Result result)
{
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { version = Version, error = message }));
    return 1;
}

/// <summary>The fixed sample data contract shared by both helpers.</summary>
internal sealed record Result(
    string Version,
    string Operation,
    Guid OrderId,
    string? Reference,
    string Source);

internal sealed record CommandLine(
    string Operation,
    Guid OrderId,
    Guid CompanyId,
    Guid UserId,
    string? Reference)
{
    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("An operation ('write' or 'read') is required.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Length - 1; i += 2)
        {
            values[args[i].TrimStart('-')] = args[i + 1];
        }

        return new CommandLine(
            args[0],
            Guid.Parse(Require(values, "order"), CultureInfo.InvariantCulture),
            values.TryGetValue("company", out var company) ? Guid.Parse(company, CultureInfo.InvariantCulture) : Guid.Empty,
            values.TryGetValue("user", out var user) ? Guid.Parse(user, CultureInfo.InvariantCulture) : Guid.Empty,
            values.TryGetValue("reference", out var reference) ? reference : null);
    }

    private static string Require(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"--{name} is required.");
}
