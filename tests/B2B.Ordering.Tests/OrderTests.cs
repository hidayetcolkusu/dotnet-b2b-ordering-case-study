using System.Net;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class OrderTests(SqlServerFixture sqlServer) : ApiTestBase(sqlServer, "orders")
{
    [Fact]
    public async Task CreatesWithCompanyPrice()
    {
        // SKU-001 lists at 100 TRY; company A negotiated 80 TRY, so 2 units cost 160 TRY.
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order("PO-100", ("SKU-001", 2)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = await response.ReadOrderAsync(Ct);
        Assert.Equal(160m, order.TotalAmount);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(SeedIds.CompanyA, order.CompanyId);
        Assert.Equal("PO-100", order.ExternalReference);
        var line = Assert.Single(order.Lines);
        Assert.Equal("SKU-001", line.Sku);
        Assert.Equal(80m, line.UnitPrice);
        Assert.Equal(160m, line.LineTotal);
        Assert.Equal($"/api/orders/{order.Id}", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NormalizesBlankExternalReferenceToNull(string reference)
    {
        // The API's own decision, deliberately separate from what the persistence layer can carry:
        // the migration experiment proves an empty string survives every schema step untouched
        // (MigrationCompatibilityTests.EmptyReferenceSurvives), while V3's normaliser treats blank
        // as "not supplied". Both are true, and confusing them is how a round-trip bug hides.
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(reference, ("SKU-001", 1)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.ReadOrderAsync(Ct);
        Assert.Null(created.ExternalReference);

        // And the read path agrees: nothing turns it back into an empty string.
        using var fetched = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{created.Id}",
                Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
            Ct);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Null((await fetched.ReadOrderAsync(Ct)).ExternalReference);
    }

    [Fact]
    public async Task CreateAndGetReturnTheSameCreatedAtUtc()
    {
        // The create response is serialised from the in-memory order and the get response from a
        // database read; both must produce the same UTC instant, Z suffix included.
        using var client = CreateClient();
        using var created = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-001", 1)));
        var order = await created.ReadOrderAsync(Ct);

        using var fetched = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{order.Id}",
                Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
            Ct);

        var reread = await fetched.ReadOrderAsync(Ct);
        Assert.Equal(DateTimeKind.Utc, reread.CreatedAtUtc.Kind);
        Assert.Equal(order.CreatedAtUtc, reread.CreatedAtUtc);
    }

    [Fact]
    public async Task GetReturnsTheLinesInTheOrderTheyWereSubmitted()
    {
        // Line order is part of the request's meaning (it is in the idempotency hash), so a read
        // must not reshuffle it. Several orders are created because a wrong ordering key could
        // still match the submitted order by chance on any single one.
        using var client = CreateClient();
        var submitted = new[] { "SKU-004", "SKU-002", "SKU-003", "SKU-001" };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var created = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
                TestData.Order(submitted.Select(sku => (sku, 1)).ToArray()));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdBody = await created.Content.ReadAsStringAsync(Ct);
            var order = await created.ReadOrderAsync(Ct);
            Assert.Equal(submitted, order.Lines.Select(line => line.Sku));

            using var fetched = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/orders/{order.Id}",
                    Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
                Ct);

            Assert.Equal(createdBody, await fetched.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task FallsBackToListPrice()
    {
        // Company B has no negotiated price on SKU-001, so it pays the 100 TRY list price.
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyBBuyer, SeedIds.CompanyB,
            TestData.Order(("SKU-001", 2)));

        var order = await response.ReadOrderAsync(Ct);
        Assert.Equal(200m, order.TotalAmount);
        Assert.Equal(100m, Assert.Single(order.Lines).UnitPrice);
    }

    [Fact]
    public async Task PriceSnapshotDoesNotChange()
    {
        using var client = CreateClient();
        using var created = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-001", 2)));
        var order = await created.ReadOrderAsync(Ct);

        await using (var maintenance = Factory.CreateMaintenanceContext())
        {
            var product = await maintenance.Db.Products.SingleAsync(p => p.Id == SeedIds.Product1, Ct);
            product.ListPrice = 999m;
            var price = await maintenance.Db.CompanyProductPrices
                .IgnoreQueryFilters()
                .SingleAsync(p => p.CompanyId == SeedIds.CompanyA && p.ProductId == SeedIds.Product1, Ct);
            price.UnitPrice = 999m;
            await maintenance.Db.SaveChangesAsync(Ct);
        }

        using var reread = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{order.Id}", Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
            Ct);

        var afterPriceChange = await reread.ReadOrderAsync(Ct);
        Assert.Equal(160m, afterPriceChange.TotalAmount);
        Assert.Equal(80m, Assert.Single(afterPriceChange.Lines).UnitPrice);
    }

    [Fact]
    public async Task RejectsEmptyLines() =>
        await AssertBadRequest(TestData.Order());

    [Fact]
    public async Task RejectsNullLinesProperty() =>
        await AssertBadRequest(new { lines = (object?)null });

    [Fact]
    public async Task RejectsNullLineElement() =>
        await AssertBadRequest(new { lines = new object?[] { null } });

    [Fact]
    public async Task RejectsNullSku() =>
        await AssertBadRequest(new { lines = new[] { new { sku = (string?)null, quantity = 1 } } });

    [Fact]
    public async Task RejectsDuplicateLines() =>
        await AssertBadRequest(TestData.Order(("SKU-001", 1), ("sku-001", 2)));

    [Fact]
    public async Task RejectsQuantityOutOfRange()
    {
        await AssertBadRequest(TestData.Order(("SKU-001", 0)));
        await AssertBadRequest(TestData.Order(("SKU-001", 1001)));
    }

    [Fact]
    public async Task RejectsTooManyLines()
    {
        var lines = Enumerable.Range(1, 101)
            .Select(i => new { sku = $"SKU-{i:000}", quantity = 1 })
            .ToArray();

        await AssertBadRequest(new { lines });
    }

    [Fact]
    public async Task RejectsUnknownProduct()
    {
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-999", 1)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectsInactiveProduct()
    {
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-005", 1)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectsAmountOverflow()
    {
        // No seeded price can overflow decimal(18,2) on its own, so the limit is reached by
        // raising one price close to the column maximum first.
        await using (var maintenance = Factory.CreateMaintenanceContext())
        {
            var product = await maintenance.Db.Products.SingleAsync(p => p.Id == SeedIds.Product4, Ct);
            product.ListPrice = 9000000000000000m;
            await maintenance.Db.SaveChangesAsync(Ct);
        }

        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyBBuyer, SeedIds.CompanyB,
            TestData.Order(("SKU-004", 2)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotCreate()
    {
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyAViewer, SeedIds.CompanyA,
            TestData.Order(("SKU-001", 1)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ViewerCanRead()
    {
        using var client = CreateClient();
        using var created = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-002", 1)));
        var order = await created.ReadOrderAsync(Ct);

        using var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/orders/{order.Id}", Tokens.ForUser(SeedIds.CompanyAViewer), SeedIds.CompanyA),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(700m, (await response.ReadOrderAsync(Ct)).TotalAmount);
    }

    [Fact]
    public async Task PagingIsStable()
    {
        using var client = CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var created = await PostOrder(client, SeedIds.CompanyBBuyer, SeedIds.CompanyB,
                TestData.Order(("SKU-003", i + 1)));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var seen = new List<Guid>();

        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/orders?page={page}&pageSize=2",
                    Tokens.ForUser(SeedIds.CompanyBBuyer), SeedIds.CompanyB),
                Ct);

            var list = await response.ReadListAsync(Ct);
            Assert.Equal(5, list.TotalCount);
            Assert.Equal(page, list.Page);
            seen.AddRange(list.Items.Select(item => item.Id));
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task VeryLargePageNumberReturnsAnEmptyPageNotAnError()
    {
        // (page - 1) * pageSize overflows int long before the caller reaches a real page, and an
        // overflowed offset must never become a 500.
        using var client = CreateClient();
        using var created = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA,
            TestData.Order(("SKU-001", 1)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        foreach (var page in new[] { 1_000_000, int.MaxValue })
        {
            using var response = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/orders?page={page}&pageSize=100",
                    Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
                Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var list = await response.ReadListAsync(Ct);
            Assert.Empty(list.Items);
            Assert.Equal(page, list.Page);
            Assert.Equal(1, list.TotalCount);
        }
    }

    [Fact]
    public async Task RejectsInvalidPaging()
    {
        using var client = CreateClient();

        foreach (var query in new[] { "?page=0", "?pageSize=0", "?pageSize=101" })
        {
            using var response = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/orders{query}",
                    Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA),
                Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private async Task AssertBadRequest(object body)
    {
        using var client = CreateClient();
        using var response = await PostOrder(client, SeedIds.CompanyABuyer, SeedIds.CompanyA, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostOrder(
        HttpClient client,
        Guid userId,
        Guid companyId,
        object body,
        string? key = null)
    {
        var request = Request(
            HttpMethod.Post,
            "/api/orders",
            Tokens.ForUser(userId),
            companyId,
            Json(body),
            key ?? Guid.NewGuid().ToString());

        return client.SendAsync(request, Ct);
    }
}
