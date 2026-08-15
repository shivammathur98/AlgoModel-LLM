namespace AlgoTrader.Persistence.Repositories;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Instruments;
using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>EF Core implementation of <see cref="IInstrumentRepository"/>.</summary>
public sealed class InstrumentRepository : IInstrumentRepository
{
    private readonly AlgoTraderDbContext _db;

    public InstrumentRepository(AlgoTraderDbContext db)
    {
        _db = db;
    }

    public async Task<Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken cancellationToken = default)
    {
        var e = await _db.Instruments
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.InstrumentToken == instrumentToken, cancellationToken);
        return e is null ? null : ToDomain(e);
    }

    public async Task<Instrument?> GetBySymbolAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
    {
        var e = await _db.Instruments
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Symbol == symbol && i.Exchange == exchange, cancellationToken);
        return e is null ? null : ToDomain(e);
    }

    public async Task<IReadOnlyList<Instrument>> GetTradableAsync(string exchange, string segment, CancellationToken cancellationToken = default)
    {
        return await _db.Instruments
            .AsNoTracking()
            .Where(i => i.Exchange == exchange && i.Segment == segment && i.IsTradable)
            .OrderBy(i => i.Symbol)
            .Select(i => new Instrument(i.InstrumentToken, i.Symbol, i.Exchange, i.Segment, i.Name, i.TickSize, i.LotSize))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken cancellationToken = default)
    {
        var tokens = instruments.Select(i => i.InstrumentToken).ToList();
        var existing = await _db.Instruments
            .Where(i => tokens.Contains(i.InstrumentToken))
            .ToDictionaryAsync(i => i.InstrumentToken, cancellationToken);

        int added = 0, updated = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var inst in instruments)
        {
            if (existing.TryGetValue(inst.InstrumentToken, out var entity))
            {
                entity.Symbol = inst.Symbol;
                entity.Exchange = inst.Exchange;
                entity.Segment = inst.Segment;
                entity.Name = inst.Name;
                entity.TickSize = inst.TickSize;
                entity.LotSize = inst.LotSize;
                entity.UpdatedAtUtc = now;
                updated++;
            }
            else
            {
                _db.Instruments.Add(new InstrumentEntity
                {
                    InstrumentToken = inst.InstrumentToken,
                    Symbol = inst.Symbol,
                    Exchange = inst.Exchange,
                    Segment = inst.Segment,
                    Name = inst.Name,
                    TickSize = inst.TickSize,
                    LotSize = inst.LotSize,
                    IsTradable = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                added++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return added + updated;
    }

    private static Instrument ToDomain(InstrumentEntity e) =>
        new(e.InstrumentToken, e.Symbol, e.Exchange, e.Segment, e.Name, e.TickSize, e.LotSize);
}
