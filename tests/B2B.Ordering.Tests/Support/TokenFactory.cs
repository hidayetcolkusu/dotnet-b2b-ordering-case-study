using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using B2B.Ordering.Api.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Mints tokens the running API actually validates. Wrong-signature and wrong-issuer cases are
/// produced by minting a real token with the wrong material, not by bypassing JwtBearer.
/// </summary>
public sealed class TokenFactory(string signingKey, string issuer, string audience)
{
    public string ForUser(Guid userId) => Create(userId, issuer, audience, signingKey, TimeSpan.FromMinutes(10));

    public string Expired(Guid userId) => Create(userId, issuer, audience, signingKey, TimeSpan.FromMinutes(-10));

    public string WrongSignature(Guid userId) =>
        Create(userId, issuer, audience, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), TimeSpan.FromMinutes(10));

    public string WrongIssuer(Guid userId) => Create(userId, "some-other-issuer", audience, signingKey, TimeSpan.FromMinutes(10));

    public string WrongAudience(Guid userId) => Create(userId, issuer, "some-other-audience", signingKey, TimeSpan.FromMinutes(10));

    /// <summary>A structurally valid token whose app_user_id claim is not a GUID.</summary>
    public string WithInvalidUserClaim() => Create(null, issuer, audience, signingKey, TimeSpan.FromMinutes(10), "not-a-guid");

    /// <summary>A structurally valid token with no app_user_id claim at all.</summary>
    public string WithoutUserClaim() => Create(null, issuer, audience, signingKey, TimeSpan.FromMinutes(10));

    /// <summary>
    /// A token that also asserts a role. The API must ignore it and use the database role, so
    /// this is how the "token role cannot override membership" test is expressed.
    /// </summary>
    public string ForUserClaimingRole(Guid userId, string role) =>
        Create(userId, issuer, audience, signingKey, TimeSpan.FromMinutes(10), extraRole: role);

    private static string Create(
        Guid? userId,
        string tokenIssuer,
        string tokenAudience,
        string key,
        TimeSpan lifetime,
        string? rawUserClaim = null,
        string? extraRole = null)
    {
        var claims = new List<Claim>();

        if (userId is not null)
        {
            claims.Add(new Claim(AuthenticationSetup.UserIdClaim, userId.Value.ToString()));
        }
        else if (rawUserClaim is not null)
        {
            claims.Add(new Claim(AuthenticationSetup.UserIdClaim, rawUserClaim));
        }

        if (extraRole is not null)
        {
            claims.Add(new Claim("role", extraRole));
            claims.Add(new Claim(ClaimTypes.Role, extraRole));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime);
        var notBefore = expires.AddMinutes(-10);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = tokenIssuer,
            Audience = tokenAudience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore,
            Expires = expires,
            IssuedAt = notBefore,
            SigningCredentials = credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
