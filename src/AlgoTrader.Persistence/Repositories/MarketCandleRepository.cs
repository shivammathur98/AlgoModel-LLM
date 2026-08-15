namespace AlgoTrader.Persistence.Repositories;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>EF Core implementation of <see cref="IMarketCandleRepository"/>.</summary>
public sealed class MarketCandleRepository : IMarketCandleRepository
{
    private readonly AlgoTraderDbContext _db;
    private readonly ILogger<MarketCandleRepository> _logger;

    public MarketCandleRepository(AlgoTraderDbContext db, ILogger<MarketCandleRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken cancellationToken = default)
    {
        if (candles.Count == 0) return 0;

        // Group candles by (InstrumentToken, Timeframe) for efficient dedup queries
        var groups = candles.GroupBy(c => (c.InstrumentToken, c.Timeframe)).ToList();
        var existingSet = new HashSet<(int Token, string Tf, DateTimeOffset Ts)>();

        foreach (var group in groups)
        {
            var token = group.Key.InstrumentToken;
            var tf = group.Key.Timeframe.ToString();
            var timestamps = group.Select(c => c.TimestampUtc).ToList();
            var minTs = timestamps.Min();
            var maxTs = timestamps.Max();

            // Query existing candles for this instrument/timeframe in the timestamp range
            var existing = await _db.MarketCandles
                .Where(c => c.InstrumentToken == token
                         && c.Timeframe == tf
                         && c.TimestampUtc >= minTs
                         && c.TimestampUtc <= maxTs)
                .Select(c => new { c.InstrumentToken, c.Timeframe, c.TimestampUtc })
                .ToListAsync(cancellationToken);

            foreach (var e in existing)
            {
                existingSet.Add((e.InstrumentToken, e.Timeframe, e.TimestampUtc));
            }
        }

        var newEntities = candles
            .Where(c => !existingSet.Contains((c.InstrumentToken, c.Timeframe.ToString(), c.TimestampUtc)))
            .Select(ToEntity)
            .ToList();

        if (newEntities.Count > 0)
        {
            await _db.MarketCandles.AddRangeAsync(newEntities, cancellationToken);
            var saved = await _db.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved {Count} new candles out of {Total} provided", saved, candles.Count);
            return saved;
        }

        _logger.LogDebug("All {Count} candles already exist — skipped", candles.Count);
        return 0;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var tf = timeframe.ToString();
        var entities = await _db.MarketCandles
            .Where(c => c.InstrumentToken == instrumentToken
                     && c.Timeframe == tf
                     && c.TimestampUtc >= fromUtc
                     && c.TimestampUtc < toUtc)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<DateTimeOffset?> GetLatestCandleTimestampAsync(
        int instrumentToken,
        Timeframe timeframe,
        CancellationToken cancellationToken = default)
    {
        var tf = timeframe.ToString();
        return await _db.MarketCandles
            .Where(c => c.InstrumentToken == instrumentToken && c.Timeframe == tf)
            .OrderByDescending(c => c.TimestampUtc)
            .Select(c => (DateTimeOffset?)c.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static MarketCandleEntity ToEntity(Candle c) => new()
    {
        InstrumentToken = c.InstrumentToken,
        Symbol = c.Symbol,
        Exchange = c.Exchange,
        Timeframe = c.Timeframe.ToString(),
        TimestampUtc = c.TimestampUtc,
        Open = c.Open,
        High = c.High,
        Low = c.Low,
        Close = c.Close,
        Volume = c.Volume
    };

    private static Candle ToDomain(MarketCandleEntity e) => new(
        InstrumentToken: e.InstrumentToken,
        Symbol: e.Symbol,
        Exchange: e.Exchange,
        Timeframe: Enum.Parse<Timeframe>(e.Timeframe),
        TimestampUtc: e.TimestampUtc,
        Open: e.Open,
        High: e.High,
        Low: e.Low,
        Close: e.Close,
        Volume: e.Volume);
}
