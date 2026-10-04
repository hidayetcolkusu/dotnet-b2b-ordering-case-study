using B2B.Ordering.Api.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Used by dotnet-ef only. It never opens a connection, so no real credentials are needed to
/// scaffold or script a migration.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=design-time;Database=design-time;Integrated Security=true")
            .Options;

        return new AppDbContext(options, new CallerContext());
    }
}
