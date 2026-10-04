using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace B2B.Ordering.Api.Shared.Auth;

public static class AuthenticationSetup
{
    /// <summary>Claim that carries the application user id. <c>X-User-Id</c> is ignored entirely.</summary>
    public const string UserIdClaim = "app_user_id";

    /// <summary>
    /// The only authentication mode this repository implements: demo tokens signed with a
    /// symmetric key that lives in local configuration — user-secrets, <c>dotnet user-jwts</c>, or
    /// a random per-fixture key in the tests. There is no identity provider integration, and
    /// adding one is out of scope for this case study.
    /// </summary>
    public const string DemoMode = "LocalSymmetricDemo";

    /// <summary>
    /// Configuration paths that can supply symmetric signing material. They exist to name the
    /// source in the refusal message; the refusal itself does not depend on any of them.
    /// </summary>
    private static readonly string[] SigningKeyPaths =
    [
        "Jwt:SigningKey",
        "Authentication:Schemes:Bearer:SigningKeys",
        "Authentication:Schemes:Bearer:SigningKey",
    ];

    /// <summary>
    /// <see cref="DemoMode"/> is the only mode on offer, so this is also the answer to "may this
    /// application start here at all".
    /// </summary>
    public static bool IsDemoModeSupported(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

        // The refusal is unconditional outside Development/Testing. Keying it off a populated
        // Jwt:SigningKey would have left two ways in: the framework binds
        // Authentication:Schemes:Bearer itself (that is how `dotnet user-jwts` keys arrive), and a
        // default demo issuer/audience with no key at all would start an API that can never
        // authenticate anyone yet still looks configured.
        if (!IsDemoModeSupported(environment))
        {
            throw new InvalidOperationException(
                $"Authentication mode '{DemoMode}' is the only mode this API implements and it is a "
                + $"local development convenience, so environment '{environment.EnvironmentName}' is "
                + "not supported and startup is refused. Configured signing key material: "
                + $"{DescribeSigningKeySources(configuration)}. Run in Development or Testing, or "
                + "integrate a real identity provider (out of scope here).");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;

                // The existing parameters object is mutated rather than replaced: the framework
                // binds Authentication:Schemes:Bearer into it first, which is how the keys written
                // by `dotnet user-jwts` reach validation.
                var validation = jwt.TokenValidationParameters;
                validation.ValidateIssuer = true;
                validation.ValidIssuer = options.Issuer;
                validation.ValidateAudience = true;
                validation.ValidAudience = options.Audience;
                validation.ValidateLifetime = true;
                validation.ClockSkew = TimeSpan.Zero;
                validation.ValidateIssuerSigningKey = true;
                validation.NameClaimType = UserIdClaim;

                if (!string.IsNullOrWhiteSpace(options.SigningKey))
                {
                    validation.IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(options.SigningKey));
                }

                jwt.Events = new JwtBearerEvents
                {
                    // A token without a usable app_user_id is not an identity we can act on, so
                    // it fails authentication (401) rather than authorisation (403).
                    OnTokenValidated = context =>
                    {
                        var value = context.Principal?.FindFirstValue(UserIdClaim);
                        if (!Guid.TryParse(value, out var userId) || userId == Guid.Empty)
                        {
                            context.Fail($"The '{UserIdClaim}' claim is missing or not a GUID.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Names the configuration paths that carry signing material, never their values. It is a
    /// diagnostic for the refusal message: an operator who set the key through the framework's own
    /// path needs to be told which path is being refused.
    /// </summary>
    private static string DescribeSigningKeySources(IConfiguration configuration)
    {
        var present = SigningKeyPaths
            .Where(path => configuration.GetSection(path).Exists())
            .ToArray();

        return present.Length == 0
            ? "none (the built-in demo defaults were in effect)"
            : string.Join(", ", present);
    }

    /// <summary>Reads the verified user id from an authenticated principal.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(UserIdClaim), out var id)
            ? id
            : throw new InvalidOperationException(
                "Authenticated principal has no valid app_user_id claim.");
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "b2b-ordering-dev";
    public string Audience { get; set; } = "b2b-ordering-api";
    public string? SigningKey { get; set; }
}
