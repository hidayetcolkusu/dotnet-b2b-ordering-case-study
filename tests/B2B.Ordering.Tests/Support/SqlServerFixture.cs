using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Owns the single SQL Server container used by the whole test run. The image is pinned by
/// digest so local and CI runs execute against exactly the same server build.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image =
        "mcr.microsoft.com/mssql/server@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb";

    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).Build();

    private readonly List<string> _createdDatabases = [];

    /// <summary>Connection string to the container's bootstrap database.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Creates a uniquely named database so independent test classes never share mutable state.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string prefix, CancellationToken ct = default)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // The name is generated above from a GUID, never from test input.
        command.CommandText = $"CREATE DATABASE [{name}];";
        await command.ExecuteNonQueryAsync(ct);

        lock (_createdDatabases)
        {
            _createdDatabases.Add(name);
        }

        return ConnectionStringFor(name);
    }

    public string ConnectionStringFor(string databaseName) =>
        new SqlConnectionStringBuilder(AdminConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;
}
