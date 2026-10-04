using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Implements the <c>--initialize-db</c> startup mode. Migrations are never applied by a normal
/// application start, so a running API can never silently reshape a database.
/// </summary>
public static class DatabaseInitializer
{
    public const string Switch = "--initialize-db";

    public static bool IsInitializeRequest(string[] args) => args.Contains(Switch);

    public static async Task RunAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{Switch} is only supported in the Development environment.");
        }

        await MigrateAndSeedAsync(app.Services);
    }

    /// <summary>
    /// The maintenance path: it applies migrations and synthetic seed data outside any request,
    /// which is why it is allowed to write rows for several companies at once.
    /// </summary>
    public static async Task MigrateAndSeedAsync(
        IServiceProvider services,
        CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync(ct);
        await SeedData.ApplyAsync(db, ct);
    }
}
