namespace AlgoTrader.Domain.Common;

/// <summary>
/// Base class for persisted entities. Provides a stable identity.
/// Audit timestamps live on the concrete entities that need them.
/// </summary>
public abstract class Entity
{
    /// <summary>Database-generated unique identifier.</summary>
    public long Id { get; set; }
}
