using System.Net;
using System.Text;
using System.Text.Json;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;

namespace B2B.Ordering.Tests;

/// <summary>
/// Every supported request path must produce the same body shape, whether the status came from an
/// exception, from JwtBearer, or from routing. UseExceptionHandler alone does not give that.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ErrorContractTests(SqlServerFixture sqlServer) : ApiTestBase(sqlServer, "errors")
{
    [Fact]
    public async Task MalformedJsonIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json("{\"lines\": [ "), "malformed");

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.MalformedRequest);
    }

    [Fact]
    public async Task UnknownJsonFieldIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json("{\"lines\":[{\"sku\":\"SKU-001\",\"quantity\":1}],\"discountPercent\":90}"),
            "unknown-field");

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.MalformedRequest);
    }

    [Fact]
    public async Task WrongContentTypeIsUnsupportedMediaType()
    {
        using var client = CreateClient();
        using var content = new StringContent("lines=1", Encoding.UTF8, "text/plain");
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA, content, "wrong-media-type");

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.UnsupportedMediaType);
    }

    [Fact]
    public async Task UnknownRouteIsNotFound()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/api/does-not-exist", Ct);

        await AssertProblem(response, HttpStatusCode.NotFound, ErrorCodes.RouteNotFound);
    }

    [Fact]
    public async Task WrongMethodIsMethodNotAllowed()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Delete, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.MethodNotAllowed, ErrorCodes.MethodNotAllowed);
    }

    [Fact]
    public async Task ValidationErrorCarriesFieldErrors()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json(TestData.Order(("SKU-001", 0))), "zero-quantity");

        using var response = await client.SendAsync(request, Ct);

        var json = await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(json.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("lines[0].quantity", out _));
    }

    [Fact]
    public async Task MissingIdempotencyKeyIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json(TestData.Order(("SKU-001", 1))));

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.IdempotencyKeyMissing);
    }

    [Fact]
    public async Task NonAsciiIdempotencyKeyIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json(TestData.Order(("SKU-001", 1))), new string('k', 129));

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.IdempotencyKeyInvalid);
    }

    [Fact]
    public async Task UnauthenticatedRequestUsesTheSameContract()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", companyId: SeedIds.CompanyA);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task ForbiddenRequestUsesTheSameContract()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyB);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.Forbidden, ErrorCodes.CompanyAccessDenied);
    }

    [Fact]
    public async Task OrderNotFoundUsesTheSameContract()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, $"/api/orders/{Guid.NewGuid()}",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.NotFound, ErrorCodes.OrderNotFound);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task ErrorBodySurvivesAnAcceptHeaderThatDoesNotMentionJson(string accept)
    {
        // A client that asks for something the API cannot produce still needs to be told why its
        // request failed. Dropping the body here would leave an integrator with a bare status code.
        using var client = CreateClient();
        using var request = Request(HttpMethod.Post, "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
            Json(TestData.Order(("SKU-001", 0))), $"accept-{accept.Replace('/', '-')}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd(accept);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/plain")]
    public async Task StatusOnlyFailuresAlsoKeepTheirBodyForAnyAccept(string accept)
    {
        // 401 comes from the challenge, not from an exception, so it travels a different path.
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", companyId: SeedIds.CompanyA);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd(accept);

        using var response = await client.SendAsync(request, Ct);

        await AssertProblem(response, HttpStatusCode.Unauthorized, ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task UnexpectedExceptionIsFiveHundredWithoutInternalDetail()
    {
        Factory.Faults.BeforeCommit = () => throw new InvalidOperationException("boom-secret-detail");

        try
        {
            using var client = CreateClient();
            using var request = Request(HttpMethod.Post, "/api/orders",
                Tokens.ForUser(SeedIds.CompanyABuyer), SeedIds.CompanyA,
                Json(TestData.Order(("SKU-001", 1))), "unexpected-error");

            using var response = await client.SendAsync(request, Ct);

            var json = await AssertProblem(
                response, HttpStatusCode.InternalServerError, ErrorCodes.InternalError);

            var raw = json.RootElement.GetRawText();
            Assert.DoesNotContain("boom-secret-detail", raw);
            Assert.DoesNotContain("InvalidOperationException", raw);
        }
        finally
        {
            Factory.Faults.Reset();
        }
    }

    private static async Task<JsonDocument> AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedErrorCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal((int)expectedStatus, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedErrorCode, json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.True(json.RootElement.TryGetProperty("title", out _));
        return json;
    }
}
