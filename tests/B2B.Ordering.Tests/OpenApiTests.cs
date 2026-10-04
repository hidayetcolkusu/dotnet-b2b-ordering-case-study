using System.Net;
using System.Text.Json;
using B2B.Ordering.Tests.Support;

namespace B2B.Ordering.Tests;

/// <summary>
/// The OpenAPI document is generated from the endpoints, so a broken schema or a transformer that
/// throws would only surface at runtime. These tests make the document part of the build.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OpenApiTests(SqlServerFixture sqlServer) : ApiTestBase(sqlServer, "openapi")
{
    [Fact]
    public async Task DocumentIsServedAnonymously()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DocumentDescribesEveryOrderOperation()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        var paths = document.RootElement.GetProperty("paths");

        var collection = paths.GetProperty("/api/orders");
        Assert.True(collection.TryGetProperty("post", out var post));
        Assert.True(collection.TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/orders/{id}").TryGetProperty("get", out _));

        // 201, plus the failures a client has to handle, are all declared.
        var responses = post.GetProperty("responses");
        foreach (var status in new[] { "201", "400", "401", "403", "409", "503" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"POST is missing a {status} response.");
        }
    }

    [Fact]
    public async Task DocumentDeclaresBearerAuthAndTheTenantHeaders()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

        // The endpoint filter reads these headers, so only the transformer can document them.
        var post = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/orders")
            .GetProperty("post");

        var headers = post.GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "header")
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("X-Company-Id", headers);
        Assert.Contains("Idempotency-Key", headers);
    }

    [Fact]
    public async Task AnonymousEndpointIsNotMarkedAsProtected()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", Ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        // Bearer is required document-wide, so /health has to opt out explicitly or the schema
        // would tell clients to authenticate for a liveness probe.
        var health = document.RootElement.GetProperty("paths").GetProperty("/health").GetProperty("get");
        Assert.True(health.TryGetProperty("security", out var security));
        Assert.Empty(security.EnumerateArray());

        var post = document.RootElement.GetProperty("paths").GetProperty("/api/orders").GetProperty("post");
        Assert.False(post.TryGetProperty("security", out _));
    }

    [Fact]
    public async Task SwaggerUiIsAvailable()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/swagger/index.html", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
