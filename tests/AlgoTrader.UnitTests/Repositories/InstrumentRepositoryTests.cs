namespace AlgoTrader.UnitTests.Repositories;

using AlgoTrader.Domain.Instruments;
using AlgoTrader.Persistence;
using AlgoTrader.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class InstrumentRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AlgoTraderDbContext _db;
    private readonly InstrumentRepository _repo;

    public InstrumentRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AlgoTraderDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AlgoTraderDbContext(options);
        _db.Database.EnsureCreated();

        _repo = new InstrumentRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Upsert_InsertsNewInstruments()
    {
        var instruments = new[]
        {
            Instrument.NseEquity(2885, "RELIANCE", "Reliance Industries"),
            Instrument.NseEquity(2953, "TCS", "Tata Consultancy Services")
        };

        var count = await _repo.UpsertAsync(instruments);

        count.Should().Be(2);
        var all = await _db.Instruments.ToListAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task Upsert_UpdatesExistingInstruments()
    {
        var instruments = new[] { Instrument.NseEquity(2885, "RELIANCE", "Reliance Industries") };
        await _repo.UpsertAsync(instruments);

        var updated = new[] { Instrument.NseEquity(2885, "RELIANCE", "Reliance Industries Ltd", 0.10m) };
        var count = await _repo.UpsertAsync(updated);

        count.Should().Be(1);
        var entity = await _db.Instruments.FirstAsync(i => i.InstrumentToken == 2885);
        entity.Name.Should().Be("Reliance Industries Ltd");
        entity.TickSize.Should().Be(0.10m);
    }

    [Fact]
    public async Task GetByToken_ReturnsCorrectInstrument()
    {
        await _repo.UpsertAsync(new[] { Instrument.NseEquity(2885, "RELIANCE", "Reliance Industries") });

        var result = await _repo.GetByTokenAsync(2885);

        result.Should().NotBeNull();
        result!.Symbol.Should().Be("RELIANCE");
    }

    [Fact]
    public async Task GetBySymbol_ReturnsCorrectInstrument()
    {
        await _repo.UpsertAsync(new[] { Instrument.NseEquity(2885, "RELIANCE", "Reliance Industries") });

        var result = await _repo.GetBySymbolAsync("RELIANCE", "NSE");

        result.Should().NotBeNull();
        result!.InstrumentToken.Should().Be(2885);
    }

    [Fact]
    public async Task GetTradable_FiltersByExchangeAndSegment()
    {
        await _repo.UpsertAsync(new[]
        {
            Instrument.NseEquity(2885, "RELIANCE", "Reliance"),
            Instrument.NseEquity(2953, "TCS", "TCS"),
            new Instrument(9999, "BSENSE", "BSE", "EQ", "BSE Sensex", 0.01m, 1)
        });

        var nseInstruments = await _repo.GetTradableAsync("NSE", "EQ");

        nseInstruments.Should().HaveCount(2);
        nseInstruments.Should().OnlyContain(i => i.Exchange == "NSE");
    }
}
