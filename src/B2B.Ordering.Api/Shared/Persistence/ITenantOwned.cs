namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>Marker for entities that belong to exactly one company.</summary>
public interface ITenantOwned
{
    Guid CompanyId { get; }
}
