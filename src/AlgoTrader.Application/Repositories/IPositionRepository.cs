namespace AlgoTrader.Application.Repositories;

using AlgoTrader.Domain.Portfolio;

/// <summary>Repository for position tracking.</summary>
public interface IPositionRepository
{
    /// <summary>Inserts a new position.</summary>
    Task<long> AddAsync(OpenPosition position, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing position (e.g., when closing).</summary>
    Task UpdateAsync(long id, OpenPosition position, string status, decimal? realizedPnl, DateTimeOffset? closedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Gets all open positions.</summary>
    Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a position by correlation ID.</summary>
    Task<(OpenPosition? Position, string? Status, decimal? RealizedPnl)> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default);
}
