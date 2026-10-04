using B2B.Ordering.Api.Shared.Persistence;

namespace B2B.Ordering.Api.Modules.Ordering.Data;

/// <summary>
/// One row per (CompanyId, UserId, Key). The row is inserted as a reservation and completed in
/// the same transaction as the order, so a committed row always carries a replayable response.
/// </summary>
public sealed class IdempotencyRecord : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public Guid? OrderId { get; set; }
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? Location { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
