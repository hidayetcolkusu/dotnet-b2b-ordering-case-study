namespace B2B.Ordering.Api.Modules.Ordering.Idempotency;

/// <summary>
/// The response as it was serialised once, inside the creating transaction. A replay writes these
/// exact bytes back, so the HTTP layer never re-serialises a stored body.
/// </summary>
public sealed record StoredResponse(int StatusCode, string Body, string Location);
