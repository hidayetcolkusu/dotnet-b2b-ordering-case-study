using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using B2B.Ordering.Api.Shared.Tenancy;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Gives each test class a migrated, seeded database of its own inside the shared container, so
/// classes that write orders never observe each other.
/// </summary>
public abstract class ApiTestBase(SqlServerFixture sqlServer, string databasePrefix) : IAsyncLifetime
{
    protected ApiFactory Factory { get; private set; } = null!;

    protected TokenFactory Tokens => Factory.Tokens;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync(databasePrefix);
        Factory = new ApiFactory(connectionString);
        await Factory.InitializeDatabaseAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Builds a request carrying a real bearer token and the target company header.</summary>
    protected static HttpRequestMessage Request(
        HttpMethod method,
        string url,
        string? token = null,
        Guid? companyId = null,
        HttpContent? content = null,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (companyId is not null)
        {
            request.Headers.Add(CompanyContextFilter.CompanyHeader, companyId.Value.ToString());
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Content = content;
        return request;
    }

    protected static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    protected static JsonContent Json<T>(T body) => JsonContent.Create(body);
}
