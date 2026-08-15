namespace AlgoTrader.Domain.Common;

/// <summary>
/// Abstraction over wall-clock time so trading logic stays deterministic and testable.
/// Business logic must use this instead of <see cref="DateTimeOffset.UtcNow"/> directly.
/// </summary>
public interface ISystemClock
{
    /// <summary>Current UTC timestamp.</summary>
    DateTimeOffset UtcNow { get; }
}
