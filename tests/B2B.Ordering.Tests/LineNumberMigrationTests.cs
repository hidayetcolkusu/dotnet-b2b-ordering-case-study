using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;
using Microsoft.Data.SqlClient;

namespace B2B.Ordering.Tests;

/// <summary>
/// S5 adds a NOT NULL column under a unique index to a table that may already hold rows, which is
/// exactly the kind of migration that passes on an empty test database and fails on a real one.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LineNumberMigrationTests(SqlServerFixture sqlServer)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingMultiLineOrdersAreNumberedByTheBackfill()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_line_number", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S4, Ct);
        await SeedAsync(connectionString);

        var orderId = Guid.CreateVersion7();
        var lineIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(connection, """
                INSERT INTO [Orders] ([Id], [CompanyId], [CreatedByUserId], [TotalAmount], [CreatedAtUtc])
                VALUES (@order, @company, @user, 3, SYSUTCDATETIME());
                """,
                ("@order", orderId), ("@company", SeedIds.CompanyA), ("@user", SeedIds.CompanyABuyer));

            foreach (var lineId in lineIds)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO [OrderLines]
                        ([Id], [OrderId], [CompanyId], [ProductId], [Quantity], [UnitPrice], [LineTotal])
                    VALUES (@line, @order, @company, @product, 1, 1, 1);
                    """,
                    ("@line", lineId), ("@order", orderId), ("@company", SeedIds.CompanyA),
                    ("@product", SeedIds.Product1));
            }
        }

        // Without the backfill, every existing line would be 0 and the unique index would fail.
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S5, Ct);

        await using var reader = new SqlConnection(connectionString);
        await reader.OpenAsync(Ct);
        await using var command = reader.CreateCommand();
        command.CommandText = "SELECT [LineNumber] FROM [OrderLines] WHERE [OrderId] = @order ORDER BY [LineNumber];";
        command.Parameters.AddWithValue("@order", orderId);

        var numbers = new List<int>();
        await using (var rows = await command.ExecuteReaderAsync(Ct))
        {
            while (await rows.ReadAsync(Ct))
            {
                numbers.Add(rows.GetInt32(0));
            }
        }

        Assert.Equal([1, 2, 3], numbers);

        // And the index now refuses a second line at an occupied position.
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(reader, """
            INSERT INTO [OrderLines]
                ([Id], [OrderId], [CompanyId], [ProductId], [LineNumber], [Quantity], [UnitPrice], [LineTotal])
            VALUES (@line, @order, @company, @product, 1, 1, 1, 1);
            """,
            ("@line", Guid.NewGuid()), ("@order", orderId), ("@company", SeedIds.CompanyA),
            ("@product", SeedIds.Product2)));

        Assert.Contains("IX_OrderLines_CompanyId_OrderId_LineNumber", duplicate.Message);
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var factory = new ApiFactory(connectionString);
        await using var maintenance = factory.CreateMaintenanceContext();
        await SeedData.ApplyAsync(maintenance.Db, Ct);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        params (string Name, Guid Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
