using B2B.Ordering.Api.Modules.Ordering.Data;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Tests;

/// <summary>
/// Referential integrity that the application layer cannot be the only guard for. The tenant write
/// guard checks the caller's company; it cannot tell whether that company or user exists at all.
/// These run through the maintenance path precisely because it bypasses the application checks —
/// what is left is what the database itself refuses.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SchemaConstraintTests(SqlServerFixture sqlServer)
    : ApiTestBase(sqlServer, "schema")
{
    [Fact]
    public async Task OrderForUnknownCompanyIsRejected()
    {
        await using var maintenance = Factory.CreateMaintenanceContext();
        maintenance.Db.Orders.Add(NewOrder(Guid.NewGuid(), SeedIds.CompanyABuyer));

        await AssertForeignKeyViolation(maintenance.Db, "FK_Orders_Companies_CompanyId");
    }

    [Fact]
    public async Task OrderForUnknownUserIsRejected()
    {
        await using var maintenance = Factory.CreateMaintenanceContext();
        maintenance.Db.Orders.Add(NewOrder(SeedIds.CompanyA, Guid.NewGuid()));

        await AssertForeignKeyViolation(maintenance.Db, "FK_Orders_Users_CreatedByUserId");
    }

    [Fact]
    public async Task CompanyPriceForUnknownCompanyIsRejected()
    {
        await using var maintenance = Factory.CreateMaintenanceContext();
        maintenance.Db.CompanyProductPrices.Add(new CompanyProductPrice
        {
            CompanyId = Guid.NewGuid(),
            ProductId = SeedIds.Product3,
            UnitPrice = 10m,
        });

        await AssertForeignKeyViolation(maintenance.Db, "FK_CompanyProductPrices_Companies_CompanyId");
    }

    [Fact]
    public async Task IdempotencyRecordForUnknownCompanyOrUserIsRejected()
    {
        await using (var unknownCompany = Factory.CreateMaintenanceContext())
        {
            unknownCompany.Db.IdempotencyRecords.Add(NewRecord(Guid.NewGuid(), SeedIds.CompanyABuyer));
            await AssertForeignKeyViolation(
                unknownCompany.Db, "FK_IdempotencyRecords_Companies_CompanyId");
        }

        await using var unknownUser = Factory.CreateMaintenanceContext();
        unknownUser.Db.IdempotencyRecords.Add(NewRecord(SeedIds.CompanyA, Guid.NewGuid()));
        await AssertForeignKeyViolation(unknownUser.Db, "FK_IdempotencyRecords_Users_UserId");
    }

    [Fact]
    public async Task OrderForKnownCompanyAndUserIsAccepted()
    {
        // The constraints must not block the legitimate case they exist to protect.
        await using var maintenance = Factory.CreateMaintenanceContext();
        var order = NewOrder(SeedIds.CompanyA, SeedIds.CompanyABuyer);
        maintenance.Db.Orders.Add(order);

        await maintenance.Db.SaveChangesAsync(Ct);

        Assert.True(await maintenance.Db.Orders.IgnoreQueryFilters()
            .AnyAsync(o => o.Id == order.Id, Ct));
    }

    private static Order NewOrder(Guid companyId, Guid userId) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = companyId,
        CreatedByUserId = userId,
        TotalAmount = 1m,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static IdempotencyRecord NewRecord(Guid companyId, Guid userId) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = companyId,
        UserId = userId,
        Key = Guid.NewGuid().ToString("N"),
        RequestHash = new string('0', 64),
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static async Task AssertForeignKeyViolation(AppDbContext db, string constraintName)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        Assert.Contains(constraintName, error.InnerException?.Message);
    }
}
