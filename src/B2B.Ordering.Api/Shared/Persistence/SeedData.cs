using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Modules.Access.Data;
using B2B.Ordering.Api.Modules.Ordering.Data;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Synthetic demo data. Runs only through the maintenance path, never from an HTTP request.
/// </summary>
public static class SeedData
{
    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        using var maintenance = MaintenanceScope.Begin();

        if (await db.Companies.AnyAsync(ct))
        {
            return;
        }

        db.Companies.AddRange(
            new Company { Id = SeedIds.CompanyA, Name = "Contoso Toptan A.S.", IsActive = true },
            new Company { Id = SeedIds.CompanyB, Name = "Fabrikam Dagitim A.S.", IsActive = true },
            new Company { Id = SeedIds.InactiveCompany, Name = "Kapali Ticaret Ltd.", IsActive = false });

        db.Users.AddRange(
            new User { Id = SeedIds.CompanyABuyer, DisplayName = "A Buyer", IsActive = true },
            new User { Id = SeedIds.CompanyAViewer, DisplayName = "A Viewer", IsActive = true },
            new User { Id = SeedIds.CompanyBBuyer, DisplayName = "B Buyer", IsActive = true },
            new User { Id = SeedIds.SharedBuyer, DisplayName = "Shared Buyer", IsActive = true },
            new User { Id = SeedIds.InactiveUser, DisplayName = "Inactive User", IsActive = false },
            new User { Id = SeedIds.RevokedMemberUser, DisplayName = "Revoked Member", IsActive = true },
            new User { Id = SeedIds.InactiveCompanyUser, DisplayName = "Closed Company User", IsActive = true });

        db.CompanyMemberships.AddRange(
            Membership(SeedIds.CompanyA, SeedIds.CompanyABuyer, CompanyRoles.Buyer, true),
            Membership(SeedIds.CompanyA, SeedIds.CompanyAViewer, CompanyRoles.Viewer, true),
            Membership(SeedIds.CompanyB, SeedIds.CompanyBBuyer, CompanyRoles.Buyer, true),
            Membership(SeedIds.CompanyA, SeedIds.SharedBuyer, CompanyRoles.Buyer, true),
            Membership(SeedIds.CompanyB, SeedIds.SharedBuyer, CompanyRoles.Buyer, true),
            Membership(SeedIds.CompanyA, SeedIds.InactiveUser, CompanyRoles.Buyer, true),
            Membership(SeedIds.CompanyA, SeedIds.RevokedMemberUser, CompanyRoles.Buyer, false),
            Membership(SeedIds.InactiveCompany, SeedIds.InactiveCompanyUser, CompanyRoles.Buyer, true));

        db.Products.AddRange(
            new Product { Id = SeedIds.Product1, Sku = "SKU-001", Name = "A4 Kagit Kolisi", ListPrice = 100m, IsActive = true },
            new Product { Id = SeedIds.Product2, Sku = "SKU-002", Name = "Toner Kartus", ListPrice = 750m, IsActive = true },
            new Product { Id = SeedIds.Product3, Sku = "SKU-003", Name = "Klasor 50'li", ListPrice = 250m, IsActive = true },
            new Product { Id = SeedIds.Product4, Sku = "SKU-004", Name = "Masaustu Organizer", ListPrice = 180m, IsActive = true },
            new Product { Id = SeedIds.InactiveProduct, Sku = "SKU-005", Name = "Uretimi Duran Kalem", ListPrice = 20m, IsActive = false });

        // Company A negotiated a better price on SKU-001; company B pays list price.
        db.CompanyProductPrices.AddRange(
            new CompanyProductPrice { CompanyId = SeedIds.CompanyA, ProductId = SeedIds.Product1, UnitPrice = 80m },
            new CompanyProductPrice { CompanyId = SeedIds.CompanyA, ProductId = SeedIds.Product2, UnitPrice = 700m },
            new CompanyProductPrice { CompanyId = SeedIds.CompanyB, ProductId = SeedIds.Product2, UnitPrice = 720m });

        await db.SaveChangesAsync(ct);
    }

    private static CompanyMembership Membership(Guid companyId, Guid userId, string role, bool isActive) =>
        new()
        {
            CompanyId = companyId,
            UserId = userId,
            Role = role,
            IsActive = isActive,
        };
}
