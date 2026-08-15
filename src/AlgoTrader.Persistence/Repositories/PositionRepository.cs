namespace AlgoTrader.Persistence.Repositories;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>EF Core implementation of <see cref="IPositionRepository"/>.</summary>
public sealed class PositionRepository : IPositionRepository
{
    private readonly AlgoTraderDbContext _db;

    public PositionRepository(AlgoTraderDbContext db)
    {
        _db = db;
    }

    public async Task<long> AddAsync(OpenPosition position, CancellationToken cancellationToken = default)
    {
        var entity = new PositionEntity
        {
            InstrumentToken = position.InstrumentToken,
            Symbol = position.Symbol,
            StrategyName = position.StrategyName,
            Quantity = position.Quantity,
            AveragePrice = position.AveragePrice,
            OpenedAtUtc = position.OpenedAtUtc,
            StopPrice = position.StopPrice,
            TargetPrice = position.TargetPrice,
            Status = "Open",
            CorrelationId = position.CorrelationId
        };

        _db.Positions.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task UpdateAsync(long id, OpenPosition position, string status, decimal? realizedPnl, DateTimeOffset? closedAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Positions.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Position {id} not found.");

        entity.Quantity = position.Quantity;
        entity.AveragePrice = position.AveragePrice;
        entity.StopPrice = position.StopPrice;
        entity.TargetPrice = position.TargetPrice;
        entity.Status = status;
        entity.RealizedPnl = realizedPnl;
        entity.ClosedAtUtc = closedAtUtc;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Positions.AsNoTracking()
            .Where(p => p.Status == "Open")
            .Select(p => new OpenPosition(
                p.InstrumentToken, p.Symbol, p.StrategyName,
                p.Quantity, p.AveragePrice, p.OpenedAtUtc,
                p.StopPrice, p.TargetPrice, p.CorrelationId))
            .ToListAsync(cancellationToken);
    }

    public async Task<(OpenPosition? Position, string? Status, decimal? RealizedPnl)> GetByCorrelationIdAsync(
        string correlationId, CancellationToken cancellationToken = default)
    {
        var e = await _db.Positions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CorrelationId == correlationId, cancellationToken);

        if (e is null) return (null, null, null);

        var position = new OpenPosition(
            e.InstrumentToken, e.Symbol, e.StrategyName,
            e.Quantity, e.AveragePrice, e.OpenedAtUtc,
            e.StopPrice, e.TargetPrice, e.CorrelationId);

        return (position, e.Status, e.RealizedPnl);
    }
}
