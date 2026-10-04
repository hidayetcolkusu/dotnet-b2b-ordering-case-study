using B2B.Ordering.Api.Shared.Persistence;

namespace B2B.Ordering.Api.Modules.Ordering.Data;

/// <summary>Company specific price that overrides <see cref="Product.ListPrice"/>.</summary>
public sealed class CompanyProductPrice : ITenantOwned
{
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }
    public decimal UnitPrice { get; set; }

    public Product Product { get; set; } = null!;
}
