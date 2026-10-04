using System.Net;
using B2B.Ordering.Api.Modules.Ordering.Data;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Api.Shared.Tenancy;
using B2B.Ordering.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Tests;

/// <summary>
/// Two independent guards are proven here: the application refuses a cross-company write, and the
/// database refuses a cross-company relationship even when the application is bypassed.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IsolationTests(SqlServerFixture sqlServer) : ApiTestBase(sqlServer, "isolation")
{
    [Fact]
    public async Task CompanyACannotReadCompanyBOrder()
    {
        var bOrderId = await CreateOrderAsync(SeedIds.CompanyBBuyer, SeedIds.CompanyB);

        using var client = CreateClient();
        using var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{bOrderId}",
                Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
            Ct);

        // Not 403: company A must not even learn that this order exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompanyAListDoesNotContainCompanyBOrders()
    {
        await CreateOrderAsync(SeedIds.CompanyBBuyer, SeedIds.CompanyB);
        var aOrderId = await CreateOrderAsync(SeedIds.CompanyABuyer, SeedIds.CompanyA);

        using var client = CreateClient();
        using var response = await client.SendAsync(
            Request(HttpMethod.Get, "/api/orders?page=1&pageSize=100",
                Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
            Ct);

        var list = await response.ReadListAsync(Ct);
        Assert.Equal(aOrderId, Assert.Single(list.Items).Id);
    }

    [Fact]
    public async Task SharedMemberSeesOnlyTheSelectedCompany()
    {
        // The same user is a Buyer in both companies; the company header selects the tenant.
        var aOrderId = await CreateOrderAsync(SeedIds.SharedBuyer, SeedIds.CompanyA);
        var bOrderId = await CreateOrderAsync(SeedIds.SharedBuyer, SeedIds.CompanyB);

        using var client = CreateClient();
        using var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{bOrderId}",
                Tokens.ForUser(SeedIds.SharedBuyer), SeedIds.CompanyA),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(aOrderId, bOrderId);
    }

    [Fact]
    public async Task DirectLineQueryIsScoped()
    {
        var bOrderId = await CreateOrderAsync(SeedIds.CompanyBBuyer, SeedIds.CompanyB);

        // A direct OrderLine query (not reached through Order) must also be filtered.
        await using var scope = Factory.Services.CreateAsyncScope();
        var caller = scope.ServiceProvider.GetRequiredService<CallerContext>();
        caller.Initialize(new Api.Modules.Access.Contracts.CompanyAccess(
            SeedIds.CompanyABuyer, SeedIds.CompanyA, "Buyer"));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.OrderLines.Where(line => line.OrderId == bOrderId).ToListAsync(Ct));
        Assert.NotEmpty(await db.OrderLines.IgnoreQueryFilters()
            .Where(line => line.OrderId == bOrderId).ToListAsync(Ct));
    }

    [Fact]
    public async Task TenantQueryWithoutContextFails()
    {
        // A missing context must fail closed, never widen the query to every company.
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Orders.ToListAsync(Ct));
    }

    [Fact]
    public async Task CrossTenantWriteRejected()
    {
        // Application guard: the caller is scoped to A, the entity claims B.
        await using var scope = Factory.Services.CreateAsyncScope();
        var caller = scope.ServiceProvider.GetRequiredService<CallerContext>();
        caller.Initialize(new Api.Modules.Access.Contracts.CompanyAccess(
            SeedIds.CompanyABuyer, SeedIds.CompanyA, "Buyer"));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Orders.Add(new Order
        {
            Id = Guid.CreateVersion7(),
            CompanyId = SeedIds.CompanyB,
            CreatedByUserId = SeedIds.CompanyABuyer,
            TotalAmount = 1m,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<TenantWriteViolationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task CrossTenantLineForeignKeyRejected()
    {
        // Database guard: even with the application check satisfied, the composite foreign key
        // refuses a line that points at another company's order.
        var bOrderId = await CreateOrderAsync(SeedIds.CompanyBBuyer, SeedIds.CompanyB);

        await using var maintenance = Factory.CreateMaintenanceContext();
        maintenance.Db.OrderLines.Add(new OrderLine
        {
            Id = Guid.CreateVersion7(),
            OrderId = bOrderId,
            CompanyId = SeedIds.CompanyA,
            ProductId = SeedIds.Product1,
            LineNumber = 1,
            Quantity = 1,
            UnitPrice = 1m,
            LineTotal = 1m,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => maintenance.Db.SaveChangesAsync(Ct));

        Assert.Contains("FK_OrderLines_Orders_CompanyId_OrderId", error.InnerException?.Message);
    }

    private async Task<Guid> CreateOrderAsync(Guid userId, Guid companyId)
    {
        using var client = CreateClient();
        using var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/orders", Tokens.ForUser(userId), companyId,
                Json(TestData.Order(("SKU-001", 1))), Guid.NewGuid().ToString()),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadOrderAsync(Ct)).Id;
    }
}
