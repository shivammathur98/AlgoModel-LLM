namespace AlgoTrader.UnitTests.Repositories;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Persistence;
using AlgoTrader.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class MarketCandleRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AlgoTraderDbContext _db;
    private readonly MarketCandleRepository _repo;

    public MarketCandleRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AlgoTraderDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AlgoTraderDbContext(options);
        _db.Database.EnsureCreated();

        _repo = new MarketCandleRepository(_db, NullLogger<MarketCandleRepository>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SaveCandles_InsertsNewCandles()
    {
        var candles = CreateCandles(789, Timeframe.Minute5, 3);

        var saved = await _repo.SaveCandlesAsync(candles);

        saved.Should().Be(3);
        var all = await _db.MarketCandles.ToListAsync();
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task SaveCandles_SkipsDuplicates()
    {
        var candles = CreateCandles(789, Timeframe.Minute5, 3);
        await _repo.SaveCandlesAsync(candles);

        // Save same candles again
        var saved = await _repo.SaveCandlesAsync(candles);

        saved.Should().Be(0);
        var all = await _db.MarketCandles.ToListAsync();
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task SaveCandles_MixedNewAndExisting()
    {
        var batch1 = CreateCandles(789, Timeframe.Minute5, 3);
        await _repo.SaveCandlesAsync(batch1);

        // batch2 overlaps on first 2 candles, adds 2 new
        var batch2 = CreateCandles(789, Timeframe.Minute5, 5);
        var saved = await _repo.SaveCandlesAsync(batch2);

        saved.Should().Be(2);
        var all = await _db.MarketCandles.ToListAsync();
        all.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetCandles_ReturnsFilteredAndOrdered()
    {
        var candles = CreateCandles(789, Timeframe.Minute5, 5);
        await _repo.SaveCandlesAsync(candles);

        var from = candles[1].TimestampUtc;
        var to = candles[4].TimestampUtc;

        var result = await _repo.GetCandlesAsync(789, Timeframe.Minute5, from, to);

        result.Should().HaveCount(3); // [1], [2], [3] — to is exclusive
        result.Should().BeInAscendingOrder(c => c.TimestampUtc);
    }

    [Fact]
    public async Task GetLatestCandleTimestamp_ReturnsMaxTimestamp()
    {
        var candles = CreateCandles(789, Timeframe.Minute5, 5);
        await _repo.SaveCandlesAsync(candles);

        var latest = await _repo.GetLatestCandleTimestampAsync(789, Timeframe.Minute5);

        latest.Should().Be(candles[4].TimestampUtc);
    }

    [Fact]
    public async Task GetLatestCandleTimestamp_ReturnsNullWhenEmpty()
    {
        var latest = await _repo.GetLatestCandleTimestampAsync(999, Timeframe.Daily);

        latest.Should().BeNull();
    }

    private static List<Candle> CreateCandles(int token, Timeframe tf, int count)
    {
        var baseTime = new DateTimeOffset(2026, 1, 15, 3, 45, 0, TimeSpan.Zero);
        var candles = new List<Candle>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new Candle(
                token, "RELIANCE", "NSE", tf,
                baseTime.AddMinutes(i * tf.Minutes()),
                100m + i, 105m + i, 99m + i, 103m + i, 100_000L + i));
        }
        return candles;
    }
}
