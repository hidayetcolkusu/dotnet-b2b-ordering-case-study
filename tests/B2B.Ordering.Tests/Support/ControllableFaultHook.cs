using B2B.Ordering.Api.Modules.Ordering.Idempotency;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Test-only fault injection. It is registered by the test host, so the running API has no way to
/// be told to fail: there is no endpoint, header or setting that reaches this.
/// </summary>
public sealed class ControllableFaultHook : ICreateOrderFaultHook
{
    public Func<Task>? AfterReservationSaved { get; set; }

    public Func<Task>? BeforeCommit { get; set; }

    public Func<Task>? AfterCommit { get; set; }

    public Task AfterReservationSavedAsync(CancellationToken ct) => Run(AfterReservationSaved);

    public Task BeforeCommitAsync(CancellationToken ct) => Run(BeforeCommit);

    public Task AfterCommitAsync(CancellationToken ct) => Run(AfterCommit);

    public void Reset()
    {
        AfterReservationSaved = null;
        BeforeCommit = null;
        AfterCommit = null;
    }

    /// <summary>Fails once, then lets the next attempt through.</summary>
    public static Func<Task> FailOnce(string message)
    {
        var fired = false;
        return () =>
        {
            if (fired)
            {
                return Task.CompletedTask;
            }

            fired = true;
            throw new InvalidOperationException(message);
        };
    }

    private static Task Run(Func<Task>? action) => action?.Invoke() ?? Task.CompletedTask;
}
