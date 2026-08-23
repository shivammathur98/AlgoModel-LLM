namespace AlgoTrader.UnitTests.MarketData;

using AlgoTrader.Domain.MarketData;
using AlgoTrader.MarketData;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the shared last-price cache (§7): it records only observed ticks, keeps the newest per
/// instrument, and never invents a price for an instrument that has not ticked.
/// </summary>
public sealed class LastPriceCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    private static Tick TickFor(int token, decimal price, DateTimeOffset at) =>
        new(token, at, price, price - 0.05m, price + 0.05m, Volume: 1_000);

    [Fact]
    public void Get_AfterUpdate_ReturnsLatestPriceAndTimestamp()
    {
        var cache = new LastPriceCache();

        cache.Update(TickFor(111, 250.50m, T0));

        var price = cache.Get(111);
        price.Should().NotBeNull();
        price!.Value.InstrumentToken.Should().Be(111);
        price.Value.Price.Should().Be(250.50m);
        price.Value.AsOfUtc.Should().Be(T0);
    }

    [Fact]
    public void Update_Twice_KeepsMostRecent()
    {
        var cache = new LastPriceCache();

        cache.Update(TickFor(111, 250.50m, T0));
        cache.Update(TickFor(111, 251.75m, T0.AddSeconds(1)));

        cache.Get(111)!.Value.Price.Should().Be(251.75m);
        cache.Get(111)!.Value.AsOfUtc.Should().Be(T0.AddSeconds(1));
    }

    [Fact]
    public void Get_ForUnseenInstrument_ReturnsNull()
    {
        var cache = new LastPriceCache();

        cache.Get(999).Should().BeNull();
        cache.TryGet(999, out _).Should().BeFalse();
    }

    [Fact]
    public void TryGet_ForSeenInstrument_ReturnsTrueAndValue()
    {
        var cache = new LastPriceCache();
        cache.Update(TickFor(222, 99.9m, T0));

        var found = cache.TryGet(222, out var price);

        found.Should().BeTrue();
        price.Price.Should().Be(99.9m);
    }

    [Fact]
    public void Update_WithNullTick_Throws()
    {
        var cache = new LastPriceCache();

        var act = () => cache.Update(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
