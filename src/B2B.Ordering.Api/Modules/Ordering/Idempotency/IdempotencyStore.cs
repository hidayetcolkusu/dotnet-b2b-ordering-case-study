using B2B.Ordering.Api.Modules.Ordering.Data;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Api.Shared.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Modules.Ordering.Idempotency;

/// <summary>
/// Everything the create flow needs to know about the idempotency table: how to recognise the
/// unique violation, how to recognise a lock timeout, and how to read a committed record back
/// through a context that is not in a failed state.
/// </summary>
public sealed class IdempotencyStore(
    DbContextOptions<AppDbContext> options,
    CallerContext caller)
{
    public const string UniqueIndexName = "UX_IdempotencyRecords_Company_User_Key";

    private const int DuplicateKeyRow = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int LockRequestTimeout = 1222;
    private const int CommandTimeout = -2;

    /// <summary>
    /// True only for a unique violation on the idempotency index. Every other database error is
    /// rethrown: swallowing them all as "duplicate" would hide real faults.
    /// </summary>
    public static bool IsIdempotencyUniqueViolation(Exception exception) =>
        FindSqlException(exception) is { } sql
        && sql.Number is DuplicateKeyRow or UniqueConstraintViolation
        && sql.Message.Contains(UniqueIndexName, StringComparison.Ordinal);

    public static bool IsLockTimeout(Exception exception) =>
        FindSqlException(exception) is { } sql
        && sql.Number is LockRequestTimeout or CommandTimeout;

    /// <summary>
    /// Reads the committed record with a brand new context. The failed context and its tracked
    /// entities are never reused.
    /// </summary>
    public async Task<IdempotencyRecord?> ReadCommittedAsync(string key, CancellationToken ct)
    {
        await using var fresh = new AppDbContext(options, caller);

        // Scoped to company AND user as well as key: the unique index has three columns, so the
        // read must use all three.
        return await fresh.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.UserId == caller.UserId && record.Key == key,
                ct);
    }

    private static SqlException? FindSqlException(Exception exception) => exception switch
    {
        SqlException sql => sql,
        { InnerException: { } inner } => FindSqlException(inner),
        _ => null,
    };
}
