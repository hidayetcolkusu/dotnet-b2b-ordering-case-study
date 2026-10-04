using System.Net;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;
using Microsoft.Data.SqlClient;

namespace B2B.Ordering.Tests;

/// <summary>
/// The compatibility experiment. Each test drives its own disposable database through the schema
/// states, so the ordinary test suite keeps running against the final schema.
///
/// What this proves: the persistence change is compatible across V1, V2 and V3 on a real SQL
/// Server. What it does not prove: zero downtime under live traffic, or that the HTTP surface of
/// old deployments behaved identically.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MigrationCompatibilityTests(SqlServerFixture sqlServer)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task V1WorksOnS1AndS2()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_v1", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S1, Ct);
        await SeedAsync(connectionString);

        var onS1 = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, onS1, "PO-S1")).Succeeded);
        Assert.Equal("PO-S1", (await ReadV1(connectionString, onS1)).Payload.Reference);

        // Expanding the schema must not disturb the old application.
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);

        Assert.Equal("PO-S1", (await ReadV1(connectionString, onS1)).Payload.Reference);

        var onS2 = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, onS2, "PO-S2")).Succeeded);
        Assert.Equal("PO-S2", (await ReadV1(connectionString, onS2)).Payload.Reference);
    }

    [Fact]
    public async Task V2ReadsV1BeforeBackfill()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_fallback", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        // Written by V1 after the expand, so only the old column is populated.
        var orderId = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, orderId, "PO-FALLBACK")).Succeeded);

        var read = (await ReadV2(connectionString, orderId)).Payload;

        Assert.Equal("PO-FALLBACK", read.Reference);
        Assert.Equal("CustomerReference", read.Source);
    }

    [Fact]
    public async Task V1ReadsV2OnS2()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_v1_reads_v2", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var orderId = Guid.CreateVersion7();
        Assert.True((await WriteV2(connectionString, orderId, "PO-BOTH")).Succeeded);

        // Two separate processes, one database: V1 still sees the value in the old column.
        var read = (await ReadV1(connectionString, orderId)).Payload;

        Assert.Equal("PO-BOTH", read.Reference);
        Assert.Equal("CustomerReference", read.Source);
    }

    [Fact]
    public async Task LateV1WriteIncludedInFinalBackfill()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_late_write", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        // A V1 instance writes one last row just before it is stopped.
        var lateOrder = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, lateOrder, "PO-LATE")).Succeeded);

        // The contract migration repeats the backfill, so the late row is not lost.
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S3, Ct);

        Assert.Equal("PO-LATE", await ReadExternalReferenceAsync(connectionString, lateOrder));
    }

    [Fact]
    public async Task NullReferenceSurvives()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_null", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var withoutReference = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, withoutReference)).Succeeded);

        var beforeContract = (await ReadV2(connectionString, withoutReference)).Payload;
        Assert.Null(beforeContract.Reference);
        Assert.Equal("none", beforeContract.Source);

        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S3, Ct);

        Assert.Null(await ReadExternalReferenceAsync(connectionString, withoutReference));
    }

    [Fact]
    public async Task LateV1WriteIsInvisibleToV3UntilTheBackfillStep()
    {
        // The expand migration copies the rows that existed at the time. A V1 instance that keeps
        // running afterwards writes only the old column, so those rows need the separate backfill
        // step before V3 may be switched on.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_backfill_step", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var lateOrder = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, lateOrder, "PO-LATE-V1")).Succeeded);

        // Before the backfill step V3 would read nothing: that is the window the step closes.
        Assert.Null(await ReadExternalReferenceAsync(connectionString, lateOrder));

        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);

        Assert.Equal("PO-LATE-V1", await ReadExternalReferenceAsync(connectionString, lateOrder));

        // V2 still reads it, and from the new column now.
        var afterBackfill = (await ReadV2(connectionString, lateOrder)).Payload;
        Assert.Equal("PO-LATE-V1", afterBackfill.Reference);
        Assert.Equal("ExternalReference", afterBackfill.Source);
    }

    [Fact]
    public async Task BackfillStepIsRepeatable()
    {
        // Re-running the backfill must be safe, because an operator may need to run it again after
        // stopping a straggling V1 instance.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_backfill_again", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, order, "PO-STRAGGLER")).Succeeded);

        await MigrationRunner.RunBackfillAsync(connectionString, Ct);
        await MigrationRunner.RunBackfillAsync(connectionString, Ct);

        Assert.Equal("PO-STRAGGLER", await ReadExternalReferenceAsync(connectionString, order));
    }

    [Fact]
    public async Task BackfillGateIsRefusedWhenTheColumnsDisagree()
    {
        // S2B is the gate that opens V3, so it must not report success on a database where an old
        // and a new value disagree. Before this check existed the gate passed silently and the
        // disagreement was only caught much later, at the contract step.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_gate_mismatch", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, order, "PO-OLD")).Succeeded);
        await SetExternalReferenceAsync(connectionString, order, "PO-NEW");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct));

        Assert.Contains("Backfill mismatch", Flatten(error), StringComparison.Ordinal);

        // Both columns survive with their own values, so the disagreement can be investigated.
        Assert.True(await ColumnExistsAsync(connectionString, "Orders", "CustomerReference"));
        Assert.Equal("PO-OLD", await ReadCustomerReferenceAsync(connectionString, order));
        Assert.Equal("PO-NEW", await ReadExternalReferenceAsync(connectionString, order));
    }

    [Fact]
    public async Task RepeatedBackfillIsRefusedWhenTheColumnsDisagree()
    {
        // The operator's repeat path is the same gate decision, so it must reach the same verdict.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_gate_repeat", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, order, "PO-OLD")).Succeeded);
        await SetExternalReferenceAsync(connectionString, order, "PO-NEW");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => MigrationRunner.RunBackfillAsync(connectionString, Ct));

        Assert.Contains("Backfill mismatch", Flatten(error), StringComparison.Ordinal);
        Assert.Equal("PO-OLD", await ReadCustomerReferenceAsync(connectionString, order));
        Assert.Equal("PO-NEW", await ReadExternalReferenceAsync(connectionString, order));
    }

    [Fact]
    public async Task BackfillGateTreatsACaseDifferenceAsADisagreement()
    {
        // The database's default collation is case-insensitive, so a plain `<>` would call these
        // two values equal and wave a genuine difference through the gate. The check forces a
        // binary collation on purpose: a reference is an opaque identifier, and a difference in
        // case is a difference. This test is what makes that decision visible rather than implied.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_gate_collation", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, order, "PO-Case")).Succeeded);
        await SetExternalReferenceAsync(connectionString, order, "PO-CASE");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct));

        Assert.Contains("Backfill mismatch", Flatten(error), StringComparison.Ordinal);
        Assert.Equal("PO-Case", await ReadCustomerReferenceAsync(connectionString, order));
    }

    [Fact]
    public async Task BackfillGateAcceptsTheStatesThatAreNotDisagreements()
    {
        // The gate must not fail the shapes a healthy migration actually produces. In particular a
        // row V3 wrote holds the new column only, with the old one null: that is correct, not a
        // mismatch, and failing it would make the gate unusable once V3 has been on.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_gate_ok", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var copied = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, copied, "PO-COPIED")).Succeeded);

        var bothWritten = Guid.CreateVersion7();
        Assert.True((await WriteV2(connectionString, bothWritten, "PO-BOTH-EQUAL")).Succeeded);

        var noReference = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, noReference)).Succeeded);

        var emptyReference = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, emptyReference, string.Empty)).Succeeded);

        var newColumnOnly = Guid.CreateVersion7();
        await InsertV3StyleRowAsync(connectionString, newColumnOnly, "PO-V3-ONLY");

        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);

        Assert.Equal("PO-COPIED", await ReadExternalReferenceAsync(connectionString, copied));
        Assert.Equal("PO-BOTH-EQUAL", await ReadExternalReferenceAsync(connectionString, bothWritten));
        Assert.Null(await ReadExternalReferenceAsync(connectionString, noReference));
        Assert.Equal(string.Empty, await ReadExternalReferenceAsync(connectionString, emptyReference));

        // The V3 row is untouched and its old column stays null.
        Assert.Equal("PO-V3-ONLY", await ReadExternalReferenceAsync(connectionString, newColumnOnly));
        Assert.Null(await ReadCustomerReferenceAsync(connectionString, newColumnOnly));

        // Running the gate again changes nothing.
        await MigrationRunner.RunBackfillAsync(connectionString, Ct);

        Assert.Equal("PO-COPIED", await ReadExternalReferenceAsync(connectionString, copied));
        Assert.Null(await ReadCustomerReferenceAsync(connectionString, newColumnOnly));
    }

    [Fact]
    public async Task EmptyReferenceSurvives()
    {
        // The persistence question, kept separate from the HTTP one: an empty string really does
        // travel from a V1 process into the old column, and no migration step turns it into null
        // or loses it. What the final API does with an empty `externalReference` is a different
        // decision, asserted in OrderTests.NormalizesBlankExternalReferenceToNull.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_empty", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();

        // A real empty command-line argument, not a null: V1 writes '' into CustomerReference.
        var write = await WriteV1(connectionString, order, string.Empty);
        Assert.True(write.Succeeded, write.StandardError);
        Assert.Equal(string.Empty, await ReadCustomerReferenceAsync(connectionString, order));
        Assert.Null(await ReadExternalReferenceAsync(connectionString, order));

        // On S2 the new column is still empty, so V2 falls back to the old one and sees ''.
        var onS2 = (await ReadV2(connectionString, order)).Payload;
        Assert.Equal(string.Empty, onS2.Reference);
        Assert.Equal("CustomerReference", onS2.Source);

        // The gate copies '' across, because '' is a value and not a missing one.
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);
        Assert.Equal(string.Empty, await ReadExternalReferenceAsync(connectionString, order));

        var afterBackfill = (await ReadV2(connectionString, order)).Payload;
        Assert.Equal(string.Empty, afterBackfill.Reference);
        Assert.Equal("ExternalReference", afterBackfill.Source);

        // And it is still '' after the contract step, not null.
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S3, Ct);
        Assert.Equal(string.Empty, await ReadExternalReferenceAsync(connectionString, order));
    }

    [Fact]
    public async Task ContractIsRefusedWhenTheColumnsStillDisagree()
    {
        // The contract migration must not drop a column whose value never reached the new one.
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_mismatch", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2Backfill, Ct);
        await SeedAsync(connectionString);

        var order = Guid.CreateVersion7();
        Assert.True((await WriteV1(connectionString, order, "PO-DISAGREE")).Succeeded);
        await SetExternalReferenceAsync(connectionString, order, "SOMETHING-ELSE");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S3, Ct));

        Assert.Contains("Backfill mismatch", Flatten(error), StringComparison.Ordinal);

        // The old column is still there, so the mismatch can be investigated.
        Assert.True(await ColumnExistsAsync(connectionString, "Orders", "CustomerReference"));
    }

    [Fact]
    public async Task V3WorksOnS2()
    {
        // S2 still carries the old column; the final API neither reads nor writes it.
        var orderId = await CreateThroughApiAsync(MigrationRunner.S2, "PO-V3-S2");

        Assert.Equal("PO-V3-S2", orderId.Reference);
    }

    [Fact]
    public async Task V3WorksOnS3()
    {
        var created = await CreateThroughApiAsync(MigrationRunner.S3, "PO-V3-S3");

        Assert.Equal("PO-V3-S3", created.Reference);
    }

    [Fact]
    public async Task ContractRejectsV1AndV2AsExpected()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_contract", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S3, Ct);
        await SeedAsync(connectionString);

        var orderId = Guid.CreateVersion7();

        var v1 = await WriteV1(connectionString, orderId, "PO-TOO-LATE");
        var v2 = await WriteV2(connectionString, orderId, "PO-TOO-LATE");

        // These failures are the expected, controlled outcome of the contract step: after the drop
        // the old binaries are incompatible, and this test asserts that rather than leaving a red
        // test behind.
        Assert.False(v1.Succeeded);
        Assert.Contains("CustomerReference", v1.StandardError, StringComparison.Ordinal);
        Assert.False(v2.Succeeded);
        Assert.Contains("CustomerReference", v2.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackToV2RequiresReverseBackfill()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("mig_rollback", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, MigrationRunner.S2, Ct);
        await SeedAsync(connectionString);

        // V3 on S2 writes the new column only.
        var orderId = Guid.CreateVersion7();
        await InsertV3StyleRowAsync(connectionString, orderId, "PO-NEW-ONLY");

        // V1 is the version that cannot see it: it reads the old column and nothing else.
        var v1BeforeSync = (await ReadV1(connectionString, orderId)).Payload;
        Assert.Null(v1BeforeSync.Reference);

        // V2 is not in the same position, and saying otherwise teaches the wrong lesson. It reads
        // the new column first, so it already sees the value before any reverse backfill runs.
        var v2BeforeSync = (await ReadV2(connectionString, orderId)).Payload;
        Assert.Equal("PO-NEW-ONLY", v2BeforeSync.Reference);
        Assert.Equal("ExternalReference", v2BeforeSync.Source);

        // The reverse backfill is still required, for the old column's own sake: V1 compatibility
        // and the S2B/S3 agreement gate both read it.
        await SyncOldColumnAsync(connectionString);

        var afterSync = (await ReadV1(connectionString, orderId)).Payload;
        Assert.Equal("PO-NEW-ONLY", afterSync.Reference);
        Assert.Equal("PO-NEW-ONLY", (await ReadV2(connectionString, orderId)).Payload.Reference);
    }

    private async Task<(Guid Id, string? Reference)> CreateThroughApiAsync(string schemaState, string reference)
    {
        var connectionString = await sqlServer.CreateDatabaseAsync($"mig_api_{schemaState[..2]}", Ct);
        await MigrationRunner.MigrateToAsync(connectionString, schemaState, Ct);

        // Today's binary stands in for V3, and it also needs the later, unrelated S5 column.
        await MigrationRunner.ApplyLineNumberChangeAsync(connectionString, Ct);

        await using var factory = new ApiFactory(connectionString);
        await using (var maintenance = factory.CreateMaintenanceContext())
        {
            await SeedData.ApplyAsync(maintenance.Db, Ct);
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(
                $$"""{"lines":[{"sku":"SKU-001","quantity":1}],"externalReference":"{{reference}}"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new("Bearer", factory.Tokens.ForUser(SeedIds.CompanyABuyer));
        request.Headers.Add("X-Company-Id", SeedIds.CompanyA.ToString());
        request.Headers.Add("Idempotency-Key", $"migration-{schemaState}");

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.ReadOrderAsync(Ct);

        // The order must also be readable back through the API on this schema state.
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{created.Id}");
        get.Headers.Authorization = new("Bearer", factory.Tokens.ForUser(SeedIds.CompanyABuyer));
        get.Headers.Add("X-Company-Id", SeedIds.CompanyA.ToString());

        using var getResponse = await client.SendAsync(get, Ct);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        return (created.Id, (await getResponse.ReadOrderAsync(Ct)).ExternalReference);
    }

    /// <summary>
    /// Companies and users must exist before any order row can be written: the Orders table has
    /// real foreign keys onto them. The experiment database therefore carries the same synthetic
    /// seed as every other test database.
    /// </summary>
    private static async Task SeedAsync(string connectionString)
    {
        await using var factory = new ApiFactory(connectionString);
        await using var maintenance = factory.CreateMaintenanceContext();
        await SeedData.ApplyAsync(maintenance.Db, Ct);
    }

    private static async Task SetExternalReferenceAsync(string connectionString, Guid orderId, string value)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [Orders] SET [ExternalReference] = @value WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", orderId);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM sys.columns
            WHERE object_id = OBJECT_ID(@table) AND name = @column;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        return (int)(await command.ExecuteScalarAsync(Ct))! > 0;
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static Task<HelperResult> WriteV1(string connectionString, Guid orderId, string? reference = null) =>
        MigrationRunner.RunV1Async(connectionString, WriteArguments(orderId, reference));

    private static Task<HelperResult> WriteV2(string connectionString, Guid orderId, string? reference = null) =>
        MigrationRunner.RunV2Async(connectionString, WriteArguments(orderId, reference));

    private static Task<HelperResult> ReadV1(string connectionString, Guid orderId) =>
        MigrationRunner.RunV1Async(connectionString, "read", "--order", orderId.ToString());

    private static Task<HelperResult> ReadV2(string connectionString, Guid orderId) =>
        MigrationRunner.RunV2Async(connectionString, "read", "--order", orderId.ToString());

    private static string[] WriteArguments(Guid orderId, string? reference)
    {
        var arguments = new List<string>
        {
            "write",
            "--order", orderId.ToString(),
            "--company", SeedIds.CompanyA.ToString(),
            "--user", SeedIds.CompanyABuyer.ToString(),
        };

        if (reference is not null)
        {
            arguments.Add("--reference");
            arguments.Add(reference);
        }

        return [.. arguments];
    }

    private static Task<string?> ReadExternalReferenceAsync(string connectionString, Guid orderId) =>
        ReadReferenceColumnAsync(connectionString, orderId, "ExternalReference");

    private static Task<string?> ReadCustomerReferenceAsync(string connectionString, Guid orderId) =>
        ReadReferenceColumnAsync(connectionString, orderId, "CustomerReference");

    /// <summary>
    /// Reads one reference column straight out of SQL, with no application layer in between: an
    /// empty string has to come back as an empty string, not as null.
    /// </summary>
    private static async Task<string?> ReadReferenceColumnAsync(
        string connectionString,
        Guid orderId,
        string column)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();

        // The column name is one of two compile-time constants in this file, never input.
        command.CommandText = $"SELECT [{column}] FROM [Orders] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", orderId);

        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>Writes the new column only, the way V3 does on S2.</summary>
    private static async Task InsertV3StyleRowAsync(string connectionString, Guid orderId, string reference)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [Orders]
                ([Id], [CompanyId], [CreatedByUserId], [TotalAmount], [CreatedAtUtc], [ExternalReference])
            VALUES (@id, @companyId, @userId, 0, SYSUTCDATETIME(), @reference);
            """;
        command.Parameters.AddWithValue("@id", orderId);
        command.Parameters.AddWithValue("@companyId", SeedIds.CompanyA);
        command.Parameters.AddWithValue("@userId", SeedIds.CompanyABuyer);
        command.Parameters.AddWithValue("@reference", reference);

        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The reverse backfill a rollback from V3 to V2 requires.</summary>
    private static async Task SyncOldColumnAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [Orders]
            SET [CustomerReference] = [ExternalReference]
            WHERE [CustomerReference] IS NULL AND [ExternalReference] IS NOT NULL;
            """;

        await command.ExecuteNonQueryAsync(Ct);
    }
}
