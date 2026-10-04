using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Shared.Persistence;

public static class PersistenceSetup
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is required. Run scripts/init-dev.ps1 to store it in user-secrets.");

        // No context pooling: CallerContext is scoped and must not be reused across requests.
        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

        return services;
    }
}
