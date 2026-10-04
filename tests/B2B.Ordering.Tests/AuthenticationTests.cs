using System.Net;
using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Tests.Support;

namespace B2B.Ordering.Tests;

/// <summary>
/// These run through the real JwtBearer handler and the real membership tables. No test
/// authentication handler is substituted, because that would prove nothing about the API.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AuthenticationTests(SqlServerFixture sqlServer)
    : ApiTestBase(sqlServer, "auth")
{
    [Fact]
    public async Task MissingTokenIsUnauthorized()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", companyId: SeedIds.CompanyA);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongSignatureIsUnauthorized() =>
        await AssertUnauthorized(Tokens.WrongSignature(SeedIds.CompanyABuyer));

    [Fact]
    public async Task ExpiredTokenIsUnauthorized() =>
        await AssertUnauthorized(Tokens.Expired(SeedIds.CompanyABuyer));

    [Fact]
    public async Task WrongIssuerIsUnauthorized() =>
        await AssertUnauthorized(Tokens.WrongIssuer(SeedIds.CompanyABuyer));

    [Fact]
    public async Task WrongAudienceIsUnauthorized() =>
        await AssertUnauthorized(Tokens.WrongAudience(SeedIds.CompanyABuyer));

    [Fact]
    public async Task NonGuidUserClaimIsUnauthorized() =>
        await AssertUnauthorized(Tokens.WithInvalidUserClaim());

    [Fact]
    public async Task MissingUserClaimIsUnauthorized() =>
        await AssertUnauthorized(Tokens.WithoutUserClaim());

    [Fact]
    public async Task ValidTokenWithoutMembershipIsForbidden()
    {
        // Company A's buyer holds a perfectly valid token, but no membership in company B.
        using var client = CreateClient();
        using var request = Request(
            HttpMethod.Get,
            "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer),
            SeedIds.CompanyB);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SpoofedUserHeaderCannotChangeIdentity()
    {
        // X-User-Id is ignored entirely: it cannot turn A's token into B's identity.
        using var client = CreateClient();
        using var request = Request(
            HttpMethod.Get,
            "/api/orders",
            Tokens.ForUser(SeedIds.CompanyABuyer),
            SeedIds.CompanyB);
        request.Headers.Add("X-User-Id", SeedIds.CompanyBBuyer.ToString());

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingCompanyHeaderIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", Tokens.ForUser(SeedIds.CompanyABuyer));

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedCompanyHeaderIsBadRequest()
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", Tokens.ForUser(SeedIds.CompanyABuyer));
        request.Headers.Add("X-Company-Id", "not-a-guid");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InactiveUserIsForbidden() =>
        await AssertForbidden(SeedIds.InactiveUser, SeedIds.CompanyA);

    [Fact]
    public async Task InactiveMembershipIsForbidden() =>
        await AssertForbidden(SeedIds.RevokedMemberUser, SeedIds.CompanyA);

    [Fact]
    public async Task InactiveCompanyIsForbidden() =>
        await AssertForbidden(SeedIds.InactiveCompanyUser, SeedIds.InactiveCompany);

    [Fact]
    public async Task TokenRoleClaimDoesNotOverrideDatabaseRole()
    {
        // A Viewer whose token claims Buyer is still a Viewer, so creating an order is refused.
        using var client = CreateClient();
        using var request = Request(
            HttpMethod.Post,
            "/api/orders",
            Tokens.ForUserClaimingRole(SeedIds.CompanyAViewer, CompanyRoles.Buyer),
            SeedIds.CompanyA,
            Json(new { lines = new[] { new { sku = "SKU-001", quantity = 1 } }, externalReference = (string?)null }),
            idempotencyKey: "role-claim-check");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HealthStaysAnonymous()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AssertUnauthorized(string token)
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", token, SeedIds.CompanyA);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task AssertForbidden(Guid userId, Guid companyId)
    {
        using var client = CreateClient();
        using var request = Request(HttpMethod.Get, "/api/orders", Tokens.ForUser(userId), companyId);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
