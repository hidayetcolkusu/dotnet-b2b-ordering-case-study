using System.Net;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Tests;

/// <summary>
/// Runs against a real SQL Server, because the whole design rests on what the database does with
/// a unique index inside a transaction. An in-memory provider would prove none of it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IdempotencyTests(SqlServerFixture sqlServer)
    : ApiTestBase(sqlServer, "idempotency")
{
    private const string OneLine = """{"lines":[{"sku":"SKU-001","quantity":2}],"externalReference":"PO-1"}""";

    [Fact]
    public async Task SamePayloadReplaysTheStoredResponse()
    {
        const string key = "replay-same-payload";
        using var client = CreateClient();

        using var first = await PostAsync(client, key, OneLine);
        using var second = await PostAsync(client, key, OneLine);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Ct),
            await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
    }

    [Fact]
    public async Task DifferentWhitespaceAndPropertyOrderStillReplays()
    {
        const string key = "replay-different-formatting";
        const string reformatted = """
            {
              "externalReference" : "PO-1",
              "lines" : [ { "quantity" : 2, "sku" : "SKU-001" } ]
            }
            """;

        using var client = CreateClient();

        using var first = await PostAsync(client, key, OneLine);
        using var second = await PostAsync(client, key, reformatted);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(Ct),
            await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
    }

    [Fact]
    public async Task DifferentQuantityIsConflict()
    {
        const string key = "conflict-different-quantity";
        using var client = CreateClient();

        using var first = await PostAsync(client, key, OneLine);
        var firstBody = await first.Content.ReadAsStringAsync(Ct);

        using var second = await PostAsync(client, key,
            """{"lines":[{"sku":"SKU-001","quantity":3}],"externalReference":"PO-1"}""");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ErrorCodes.IdempotencyKeyConflict, await second.Content.ReadAsStringAsync(Ct));

        // The first order is untouched.
        using var reread = await PostAsync(client, key, OneLine);
        Assert.Equal(firstBody, await reread.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
    }

    [Fact]
    public async Task ReorderedLinesAreADifferentPayload()
    {
        const string key = "conflict-line-order";
        const string ab = """{"lines":[{"sku":"SKU-001","quantity":1},{"sku":"SKU-002","quantity":1}]}""";
        const string ba = """{"lines":[{"sku":"SKU-002","quantity":1},{"sku":"SKU-001","quantity":1}]}""";

        using var client = CreateClient();

        using var first = await PostAsync(client, key, ab);
        using var second = await PostAsync(client, key, ba);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SameKeyForAnotherUserOrCompanyIsIndependent()
    {
        const string key = "shared-key";
        using var client = CreateClient();

        using var a = await PostAsync(client, key, OneLine);
        using var b = await PostAsync(client, key, OneLine, SeedIds.SharedBuyer, SeedIds.CompanyA);
        using var c = await PostAsync(client, key, OneLine, SeedIds.CompanyBBuyer, SeedIds.CompanyB);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.Equal(HttpStatusCode.Created, c.StatusCode);

        var ids = new[]
        {
            (await a.ReadOrderAsync(Ct)).Id,
            (await b.ReadOrderAsync(Ct)).Id,
            (await c.ReadOrderAsync(Ct)).Id,
        };

        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task TenConcurrentIdenticalRequestsProduceOneOrder()
    {
        const string key = "race-identical";
        using var client = CreateClient();

        // A barrier starts the clients together without any sleep-based guessing.
        using var barrier = new Barrier(10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await Task.Yield();
            barrier.SignalAndWait(Ct);
            return await PostAsync(client, key, OneLine);
        }));

        try
        {
            Assert.All(responses, response =>
                Assert.Equal(HttpStatusCode.Created, response.StatusCode));

            var bodies = new List<string>();
            foreach (var response in responses)
            {
                bodies.Add(await response.Content.ReadAsStringAsync(Ct));
            }

            Assert.Single(bodies.Distinct());
            Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));

            await using var maintenance = Factory.CreateMaintenanceContext();
            var record = await maintenance.Db.IdempotencyRecords
                .IgnoreQueryFilters()
                .SingleAsync(r => r.CompanyId == SeedIds.CompanyA
                    && r.UserId == SeedIds.CompanyABuyer
                    && r.Key == key, Ct);

            Assert.NotNull(record.OrderId);
            Assert.Equal(201, record.StatusCode);
            Assert.False(string.IsNullOrEmpty(record.ResponseBody));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentDifferentPayloadsHaveOneWinner()
    {
        const string key = "race-different-payloads";
        using var client = CreateClient();
        using var barrier = new Barrier(2);

        var payloads = new[]
        {
            OneLine,
            """{"lines":[{"sku":"SKU-003","quantity":7}]}""",
        };

        var responses = await Task.WhenAll(payloads.Select(async payload =>
        {
            await Task.Yield();
            barrier.SignalAndWait(Ct);
            return await PostAsync(client, key, payload);
        }));

        try
        {
            var statuses = responses.Select(r => r.StatusCode).ToArray();
            Assert.Single(statuses, HttpStatusCode.Created);
            Assert.Single(statuses, HttpStatusCode.Conflict);
            Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task FailureBeforeCommitLeavesNothingBehind()
    {
        const string key = "fault-before-commit";
        Factory.Faults.BeforeCommit = ControllableFaultHook.FailOnce("injected pre-commit failure");

        try
        {
            using var client = CreateClient();

            using var failed = await PostAsync(client, key, OneLine);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

            Assert.Equal(0, await CountOrdersAsync(SeedIds.CompanyA));
            Assert.Equal(0, await CountRecordsAsync(key));

            // The rollback left no reservation, so the retry is a clean first attempt.
            using var retried = await PostAsync(client, key, OneLine);
            Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
            Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
        }
        finally
        {
            Factory.Faults.Reset();
        }
    }

    [Fact]
    public async Task LostResponseAfterCommitIsRecoveredByReplay()
    {
        const string key = "fault-after-commit";
        Factory.Faults.AfterCommit = ControllableFaultHook.FailOnce("connection lost after commit");

        try
        {
            using var client = CreateClient();

            using var lost = await PostAsync(client, key, OneLine);
            Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);

            // The order was committed even though the caller never saw the response.
            Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));

            using var replay = await PostAsync(client, key, OneLine);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(1, await CountOrdersAsync(SeedIds.CompanyA));
        }
        finally
        {
            Factory.Faults.Reset();
        }
    }

    [Fact]
    public async Task RevokedMembershipCannotReplayTheStoredResponse()
    {
        const string key = "replay-after-revoke";
        using var client = CreateClient();

        using var created = await PostAsync(client, key, OneLine);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await using (var maintenance = Factory.CreateMaintenanceContext())
        {
            var membership = await maintenance.Db.CompanyMemberships.SingleAsync(
                m => m.CompanyId == SeedIds.CompanyA && m.UserId == SeedIds.CompanyABuyer, Ct);
            membership.IsActive = false;
            await maintenance.Db.SaveChangesAsync(Ct);
        }

        using var replay = await PostAsync(client, key, OneLine);

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.DoesNotContain("totalAmount", await replay.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task LockWaitTimeoutIsServiceUnavailableWithRetryAfter()
    {
        const string key = "lock-timeout";

        // A second host against the same database, with a short lock budget.
        await using var impatient = new ApiFactory(Factory.ConnectionString)
        {
            LockTimeoutMilliseconds = 500,
        };

        // Hold the reservation row in an uncommitted transaction from outside the API.
        await using var connection = new SqlConnection(Factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var blocking = (SqlTransaction)await connection.BeginTransactionAsync(Ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = blocking;
            insert.CommandText = """
                INSERT INTO [IdempotencyRecords]
                    ([Id], [CompanyId], [UserId], [Key], [RequestHash], [CreatedAtUtc])
                VALUES (@id, @companyId, @userId, @key, @hash, SYSUTCDATETIME());
                """;
            insert.Parameters.AddWithValue("@id", Guid.NewGuid());
            insert.Parameters.AddWithValue("@companyId", SeedIds.CompanyA);
            insert.Parameters.AddWithValue("@userId", SeedIds.CompanyABuyer);
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@hash", new string('0', 64));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        using var client = impatient.CreateClient();
        // The second host signs with its own random key, so its own token factory must be used.
        using var response = await PostAsync(client, key, OneLine, tokens: impatient.Tokens);

        await blocking.RollbackAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
        Assert.Contains(ErrorCodes.IdempotencyBusy, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string key,
        string body,
        Guid? userId = null,
        Guid? companyId = null,
        TokenFactory? tokens = null)
    {
        var request = Request(
            HttpMethod.Post,
            "/api/orders",
            (tokens ?? Tokens).ForUser(userId ?? SeedIds.CompanyABuyer),
            companyId ?? SeedIds.CompanyA,
            Json(body),
            key);

        return client.SendAsync(request, Ct);
    }

    private async Task<int> CountOrdersAsync(Guid companyId)
    {
        await using var maintenance = Factory.CreateMaintenanceContext();
        return await maintenance.Db.Orders
            .IgnoreQueryFilters()
            .CountAsync(order => order.CompanyId == companyId, Ct);
    }

    private async Task<int> CountRecordsAsync(string key)
    {
        await using var maintenance = Factory.CreateMaintenanceContext();
        return await maintenance.Db.IdempotencyRecords
            .IgnoreQueryFilters()
            .CountAsync(record => record.Key == key, Ct);
    }
}
