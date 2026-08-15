namespace AlgoTrader.Infrastructure;

using AlgoTrader.Domain.Common;

/// <summary>
/// Production system clock returning the real wall-clock time. Tests inject a deterministic fake.
/// </summary>
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
