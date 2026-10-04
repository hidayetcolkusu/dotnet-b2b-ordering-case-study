namespace B2B.Ordering.Api.Modules.Ordering.Data;

/// <summary>Catalogue product. Not tenant scoped: the catalogue is shared.</summary>
public sealed class Product
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal ListPrice { get; set; }
    public bool IsActive { get; set; }
}
