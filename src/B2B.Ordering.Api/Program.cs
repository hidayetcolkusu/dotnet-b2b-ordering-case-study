using System.Text.Json.Serialization;
using B2B.Ordering.Api.Modules.Access;
using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Modules.Ordering;
using B2B.Ordering.Api.Modules.Ordering.Create;
using B2B.Ordering.Api.Modules.Ordering.Idempotency;
using B2B.Ordering.Api.Modules.Ordering.Read;
using B2B.Ordering.Api.Shared.Auth;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.OpenApi;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Api.Shared.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddScoped<CallerContext>();
builder.Services.AddScoped<ICompanyAccessResolver, CompanyAccessResolver>();
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<IdempotencyStore>();
builder.Services.AddSingleton<ICreateOrderFaultHook, NoFaultHook>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Idempotency").Get<IdempotencyOptions>() ?? new IdempotencyOptions());
builder.Services.AddScoped<GetOrderHandler>();
builder.Services.AddScoped<ListOrdersHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddApiProblemDetails();
builder.Services.AddApiOpenApi();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Unknown JSON members are rejected: a silently ignored field would become a hidden business
    // input that never reaches the idempotency hash.
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var app = builder.Build();

if (DatabaseInitializer.IsInitializeRequest(args))
{
    // Development-only bootstrap mode: migrate, seed, exit. A normal start never migrates.
    await DatabaseInitializer.RunAsync(app);
    return;
}

// Pipeline order is deliberate: both error doors are outermost, then routing, then identity,
// then authorisation. Company context runs as an endpoint filter after authorisation, so a
// missing or malformed company header can never shadow a 401.
app.UseExceptionHandler();
app.UseApiStatusCodeProblems();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Schema and Swagger UI, in Development and Testing only.
app.UseApiOpenApi();

// Anonymous process liveness only. It must never leak configuration or connection details.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous()
    .WithTags("Diagnostics")
    .WithSummary("Process liveness")
    .WithDescription("Anonymous. Returns no configuration, connection or version detail.");

app.MapOrderEndpoints();

app.Run();

/// <summary>Exposed so the integration test project can host the real pipeline.</summary>
public partial class Program;
