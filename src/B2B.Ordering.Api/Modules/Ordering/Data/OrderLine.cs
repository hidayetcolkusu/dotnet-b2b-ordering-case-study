using B2B.Ordering.Api.Shared.Persistence;

namespace B2B.Ordering.Api.Modules.Ordering.Data;

/// <summary>
/// Carries its own CompanyId so that the composite foreign key to (CompanyId, OrderId) makes
/// attaching a line to another company's order impossible at the database level.
/// </summary>
public sealed class OrderLine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProductId { get; set; }

    /// <summary>
    /// 1-based position in the submitted request. Line order is part of the request's meaning, and
    /// no other column preserves it: a version 7 Guid id does not sort by creation in SQL Server.
    /// </summary>
    public int LineNumber { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    public Order Order { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
