using B2B.Ordering.Api.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace B2B.Ordering.Tests;

/// <summary>
/// Startup and configuration tests for the one authentication mode this repository implements.
/// None of them needs a database: the refusal happens while services are being registered, long
/// before anything opens a connection.
///
/// The point they defend is narrow and worth stating exactly: the demo mode is refused outside
/// Development and Testing <em>whichever configuration path supplies the key</em> — and also when
/// nothing supplies one at all, because the built-in demo issuer and audience are themselves the
/// unsupported mode. It is not a claim about what an unverified token would have been able to do.
/// </summary>
public sealed class AuthenticationStartupTests
{
    private const string Issuer = "b2b-ordering-dev";
    private const string Audience = "b2b-ordering-api";

    /// <summary>Synthetic, and the only key material in this file. It reaches no database.</summary>
    private const string SigningKey = "startup-probe-key-not-a-real-secret-0123456789abcdef";

    /// <summary>A connection string that is never opened; startup is refused before persistence runs.</summary>
    private const string UnusedConnectionString =
        "Server=127.0.0.1,1;Database=StartupProbeNotUsed;Integrated Security=True;Encrypt=False";

    public static TheoryData<string, string> RefusedCombinations()
    {
        var data = new TheoryData<string, string>();

        foreach (var environment in new[] { "Production", "Staging" })
        {
            foreach (var keySource in new[] { KeySource.DemoDefaults, KeySource.JwtSection, KeySource.UserJwts })
            {
                data.Add(environment, keySource);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedCombinations))]
    public void StartupIsRefusedOutsideDevelopmentAndTesting(string environment, string keySource)
    {
        var configuration = BuildConfiguration(keySource);

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddApiAuthentication(configuration, new StubEnvironment(environment)));

        Assert.Contains(environment, error.Message, StringComparison.Ordinal);
        Assert.Contains(AuthenticationSetup.DemoMode, error.Message, StringComparison.Ordinal);

        // The refusal names the configuration path, never the key itself.
        Assert.DoesNotContain(SigningKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DemoModeIsAcceptedInDevelopmentAndTesting(string environment)
    {
        var configuration = BuildConfiguration(KeySource.JwtSection);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddApiAuthentication(configuration, new StubEnvironment(environment));

        using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(Issuer, jwt.TokenValidationParameters.ValidIssuer);
        Assert.Equal(Audience, jwt.TokenValidationParameters.ValidAudience);
        Assert.True(jwt.TokenValidationParameters.ValidateIssuer);
        Assert.True(jwt.TokenValidationParameters.ValidateAudience);
        Assert.True(jwt.TokenValidationParameters.ValidateLifetime);
        Assert.True(jwt.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal(TimeSpan.Zero, jwt.TokenValidationParameters.ClockSkew);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task TheRealHostRefusesToStart(string environment)
    {
        // The registration-level tests above call one method. This one builds the actual Program
        // pipeline, so a guard that only covered the registration method could not let the real
        // host reach "Application started" in Production. No signing key is configured here: the
        // demo defaults alone are already the unsupported mode.
        await using var probe = new HostProbe(environment, includeSigningKey: false);

        var error = Assert.ThrowsAny<Exception>(() => probe.CreateClient());

        Assert.Contains("startup is refused", Flatten(error), StringComparison.Ordinal);
        Assert.Contains(environment, Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealHostStartsInDevelopment()
    {
        // The other half of the acceptance: a valid local configuration still runs. /health is
        // anonymous and touches no database, so this proves startup and nothing more.
        await using var probe = new HostProbe("Development", includeSigningKey: true);
        using var client = probe.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static IConfiguration BuildConfiguration(string keySource) =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings(keySource)).Build();

    private static Dictionary<string, string?> Settings(string keySource) => keySource switch
    {
        // Nothing configured at all: the built-in demo issuer and audience are in effect.
        KeySource.DemoDefaults => new Dictionary<string, string?>(),

        KeySource.JwtSection => new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = Issuer,
            ["Jwt:Audience"] = Audience,
            ["Jwt:SigningKey"] = SigningKey,
        },

        // The shape `dotnet user-jwts` writes, which the framework binds by itself.
        KeySource.UserJwts => new Dictionary<string, string?>
        {
            ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Id"] = "startup-probe",
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = SigningKey,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Length"] = "32",
        },

        _ => throw new ArgumentOutOfRangeException(nameof(keySource), keySource, "Unknown key source."),
    };

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static class KeySource
    {
        public const string DemoDefaults = "demo-defaults";
        public const string JwtSection = "jwt-section";
        public const string UserJwts = "user-jwts";
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "B2B.Ordering.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Hosts the real <c>Program</c> with a synthetic connection string that is never opened.</summary>
    private sealed class HostProbe(string environment, bool includeSigningKey)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting, not ConfigureAppConfiguration: Program reads configuration while the
            // WebApplicationBuilder is still being built.
            builder.UseSetting("environment", environment);
            builder.UseSetting("ConnectionStrings:Default", UnusedConnectionString);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Audience);

            if (includeSigningKey)
            {
                builder.UseSetting("Jwt:SigningKey", SigningKey);
            }
        }
    }
}
