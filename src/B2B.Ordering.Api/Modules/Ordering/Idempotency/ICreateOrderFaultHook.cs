namespace B2B.Ordering.Api.Modules.Ordering.Idempotency;

/// <summary>
/// A seam for the tests that need to fail the flow at an exact point (after the reservation was
/// saved, just before commit, just after commit). The production registration does nothing and no
/// endpoint can trigger a fault, so this cannot be reached from outside the process.
/// </summary>
public interface ICreateOrderFaultHook
{
    Task AfterReservationSavedAsync(CancellationToken ct) => Task.CompletedTask;

    Task BeforeCommitAsync(CancellationToken ct) => Task.CompletedTask;

    Task AfterCommitAsync(CancellationToken ct) => Task.CompletedTask;
}

public sealed class NoFaultHook : ICreateOrderFaultHook;
