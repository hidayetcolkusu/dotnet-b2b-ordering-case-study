using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using B2B.Ordering.Api.Migrations;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Api.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Drives the schema to a chosen state (S1, S2, S3) and runs the old-version helper executables
/// against it. The helpers really are separate operating system processes: nothing in the test is
/// simulating what an old deployment would do.
/// </summary>
public static class MigrationRunner
{
    public const string S1 = "S1_Initial";
    public const string S2 = "S2_ExpandExternalReference";

    /// <summary>
    /// The final backfill, applied after the last V1 and V2 writer has stopped and before V3 is
    /// switched on. It is its own migration because it is its own operational step, not something
    /// that happens as a side effect of expanding or contracting.
    /// </summary>
    public const string S2Backfill = "S2B_BackfillExternalReference";

    public const string S3 = "S3_ContractDropCustomerReference";

    /// <summary>Ordinary later changes, outside the reference experiment.</summary>
    public const string S4 = "S4_AddCompanyAndUserForeignKeys";

    public const string S5 = "S5_AddOrderLineNumber";

    /// <summary>
    /// The same two statements the backfill migration runs, taken from the migration itself rather
    /// than restated here. The operator path has to include the agreement check as well: a repeat
    /// run after stopping a straggler is the same gate decision, so it must reach the same verdict.
    /// </summary>
    public static readonly string BackfillSql =
        S2B_BackfillExternalReference.CopySql
        + Environment.NewLine
        + S2B_BackfillExternalReference.AgreementCheckSql(
            "Backfill gate aborted; both columns were left in place.");

    /// <summary>Applies migrations up to (or back down to) the named schema state.</summary>
    public static async Task MigrateToAsync(string connectionString, string targetMigration, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        await using var db = new AppDbContext(options, new CallerContext());
        await db.GetService<IMigrator>().MigrateAsync(targetMigration, cancellationToken: ct);
    }

    /// <summary>
    /// Applies S5's own Up operations to a database parked at an experiment state, without
    /// recording it in the migration history.
    ///
    /// The experiment uses today's API binary as "V3". S5 came later and adds a column that binary
    /// now reads, so on a bare S2 or S3 database it would fail on <c>LineNumber</c> — a fact about
    /// S5, not about the reference column under test. S5 is purely additive and touches only
    /// <c>OrderLines</c>, which no step of the experiment changes, so applying it on top keeps the
    /// question the V3 tests ask unchanged.
    /// </summary>
    public static async Task ApplyLineNumberChangeAsync(string connectionString, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        await using var db = new AppDbContext(options, new CallerContext());

        var assembly = db.GetService<IMigrationsAssembly>();
        var id = assembly.Migrations.Keys.Single(key => key.EndsWith("_" + S5, StringComparison.Ordinal));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations);

        await db.GetService<IMigrationCommandExecutor>()
            .ExecuteNonQueryAsync(commands, db.GetService<IRelationalConnection>(), ct);
    }

    /// <summary>
    /// Runs the backfill step again, the way an operator would. It is wrapped in a transaction for
    /// the same reason the migration is: if the agreement check fails, the copy it just made is
    /// rolled back and the database is left exactly as it was for investigation.
    /// </summary>
    public static async Task RunBackfillAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BackfillSql;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public static Task<HelperResult> RunV1Async(string connectionString, params string[] args) =>
        RunAsync("Migration.V1", connectionString, args);

    public static Task<HelperResult> RunV2Async(string connectionString, params string[] args) =>
        RunAsync("Migration.V2", connectionString, args);

    private static async Task<HelperResult> RunAsync(
        string helper,
        string connectionString,
        string[] args)
    {
        var startInfo = new ProcessStartInfo(HelperPath(helper))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The connection string travels in the environment, never on the command line or stdout.
        startInfo.Environment["MIGRATION_DB_CONNECTION"] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {helper}.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new HelperResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static string HelperPath(string helper)
    {
        // The test binary sits at tests/<project>/bin/<config>/<tfm>; the helper is built to the
        // same configuration and framework under samples/<helper>/bin/....
        var testBinary = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = testBinary.Name;
        var configuration = testBinary.Parent!.Name;

        var repoRoot = testBinary;
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "global.json")))
        {
            repoRoot = repoRoot.Parent;
        }

        if (repoRoot is null)
        {
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
        }

        var executable = OperatingSystem.IsWindows() ? $"{helper}.exe" : helper;
        var path = Path.Combine(repoRoot.FullName, "samples", helper, "bin", configuration, framework, executable);

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"The {helper} executable was not built at {path}.", path);
    }
}

public sealed record HelperResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public HelperPayload Payload => JsonSerializer.Deserialize<HelperPayload>(
        StandardOutput,
        TestData.Json) ?? throw new InvalidOperationException($"Helper produced no JSON: {StandardError}");
}

public sealed record HelperPayload(
    string Version,
    string Operation,
    Guid OrderId,
    string? Reference,
    string Source);
