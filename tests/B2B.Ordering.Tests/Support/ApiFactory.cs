using System.Security.Cryptography;
using B2B.Ordering.Api.Modules.Ordering.Idempotency;
using B2B.Ordering.Api.Shared.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Hosts the real API pipeline (real JwtBearer, real EF Core, real SQL Server) against a database
/// that belongs to the test run. The signing key is random per factory, so a token minted for one
/// test class cannot be accepted by another.
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string Issuer = "b2b-ordering-tests";
    public const string Audience = "b2b-ordering-api";

    public string ConnectionString { get; } = connectionString;

    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public TokenFactory Tokens => new(SigningKey, Issuer, Audience);

    /// <summary>Test-only fault injection seam; see <see cref="ControllableFaultHook"/>.</summary>
    public ControllableFaultHook Faults { get; } = new();

    /// <summary>Lock wait budget for the create transaction, in milliseconds.</summary>
    public int LockTimeoutMilliseconds { get; init; } = 5000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICreateOrderFaultHook>();
            services.AddSingleton<ICreateOrderFaultHook>(Faults);
        });

        // UseSetting (not ConfigureAppConfiguration) because Program reads configuration while
        // building the WebApplicationBuilder, before deferred configuration callbacks run.
        builder.UseSetting("environment", "Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting(
            "Idempotency:LockTimeoutMilliseconds",
            LockTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Applies migrations and synthetic seed data through the maintenance path.</summary>
    public async Task InitializeDatabaseAsync(CancellationToken ct = default)
    {
        await DatabaseInitializer.MigrateAndSeedAsync(Services, ct);
    }

    /// <summary>
    /// Opens a maintenance context for assertions that must see across companies. Nothing in the
    /// request pipeline can reach this path.
    /// </summary>
    public MaintenanceContext CreateMaintenanceContext() => new(Services);
}
