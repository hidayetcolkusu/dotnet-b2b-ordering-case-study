using B2B.Ordering.Api.Shared.Persistence;

namespace B2B.Ordering.Api.Modules.Ordering.Data;

/// <summary>
/// Orders are immutable once created: there is no update or delete endpoint. That keeps the
/// reference column migration to one race — new rows from a V1 instance still running after the
/// expand — which is what the separate S2B backfill step closes; no update can rewrite a row
/// already copied.
/// </summary>
public sealed class Order : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Final (V3) persistence name for the caller supplied external reference.</summary>
    public string? ExternalReference { get; set; }

    public List<OrderLine> Lines { get; set; } = [];
}
