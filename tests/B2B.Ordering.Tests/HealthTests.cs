using System.Net;
using B2B.Ordering.Tests.Support;

namespace B2B.Ordering.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class HealthTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await sqlServer.CreateDatabaseAsync("health");
        _factory = new ApiFactory(connectionString);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task HealthEndpointIsAnonymousAndHealthy()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
