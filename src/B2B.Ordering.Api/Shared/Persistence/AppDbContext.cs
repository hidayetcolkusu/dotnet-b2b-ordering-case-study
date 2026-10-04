using B2B.Ordering.Api.Modules.Access.Data;
using B2B.Ordering.Api.Modules.Ordering.Data;
using B2B.Ordering.Api.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// The single EF Core context. Merging both modules' mappings here is the documented
/// infrastructure exception to the module boundary (see ADR 0001): mappings are shared, but
/// Ordering code still never reaches Access entities.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, CallerContext caller)
    : DbContext(options)
{
    private readonly CallerContext _caller = caller;

    // Access module. Bootstrap tables: deliberately free of tenant query filters, because the
    // membership lookup must run before any tenant context exists.
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CompanyMembership> CompanyMemberships => Set<CompanyMembership>();

    // Ordering module.
    public DbSet<Product> Products => Set<Product>();
    public DbSet<CompanyProductPrice> CompanyProductPrices => Set<CompanyProductPrice>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>
    /// Read by every tenant query filter. Throwing (instead of returning Guid.Empty) is the
    /// point: a missing context must fail the request, never widen it to all companies.
    /// </summary>
    public Guid TenantCompanyId => _caller.CompanyId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<CompanyProductPrice>()
            .HasQueryFilter(x => x.CompanyId == TenantCompanyId);
        modelBuilder.Entity<Order>()
            .HasQueryFilter(x => x.CompanyId == TenantCompanyId);
        modelBuilder.Entity<OrderLine>()
            .HasQueryFilter(x => x.CompanyId == TenantCompanyId);
        modelBuilder.Entity<IdempotencyRecord>()
            .HasQueryFilter(x => x.CompanyId == TenantCompanyId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardTenantWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Query filters only protect reads. This closes the write side: no tracked tenant entity may
    /// be added or modified for a company other than the caller's.
    /// </summary>
    private void GuardTenantWrites()
    {
        if (MaintenanceScope.IsActive)
        {
            return;
        }

        var pending = ChangeTracker.Entries<ITenantOwned>()
            .Where(entry => entry.State is EntityState.Added
                or EntityState.Modified
                or EntityState.Deleted)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var callerCompanyId = _caller.CompanyId;

        foreach (var entry in pending)
        {
            if (entry.Entity.CompanyId != callerCompanyId)
            {
                throw new TenantWriteViolationException(
                    entry.Entity.GetType().Name,
                    entry.Entity.CompanyId,
                    callerCompanyId);
            }
        }
    }
}
