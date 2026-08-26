namespace AlgoTrader.Application.Execution;

using System.Threading;

/// <summary>
/// Default <see cref="IOrderMutationGate"/> — a single <see cref="SemaphoreSlim"/> with one permit.
/// <b>Register as a singleton</b> so order submission and every asynchronous reconciliation share one gate
/// across DI scopes (the AUDIT-0009 fix depends on the instance being shared process-wide).
/// </summary>
public sealed class OrderMutationGate : IOrderMutationGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    /// <summary>Releases the permit exactly once, even if disposed more than once.</summary>
    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
