namespace AlgoTrader.UnitTests.MarketData;

using System.Buffers.Binary;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.MarketData.Kite;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the pure Kite binary tick decoder (<see cref="KiteWebSocketMarketDataProvider.TryParsePacket"/>) —
/// the unit that turns one raw packet into a <see cref="Tick"/> and, for full-mode packets, a
/// <see cref="MarketDepth"/> order-book snapshot (§7). It must decode paise→rupee prices, read day volume from
/// the correct offset, expose the 5x2 book in order, derive best bid/ask from the top of that book, and degrade
/// gracefully for shorter (LTP/quote) and malformed packets. The socket plumbing around it is thin I/O and is
/// left untested by convention.
/// </summary>
public sealed class KiteWebSocketDepthParsingTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 15, 4, 45, 0, TimeSpan.Zero);

    [Fact]
    public void TryParsePacket_FullMode_DecodesBookAndDerivesBestBidAsk()
    {
        var packet = BuildFullPacket(
            token: 738561,
            ltpPaise: 25050,       // 250.50
            volume: 123456,
            bids: new[] { (100, 25040, 3), (80, 25030, 2), (60, 25020, 2), (40, 25010, 1), (20, 25000, 1) },
            asks: new[] { (120, 25060, 4), (90, 25070, 3), (70, 25080, 2), (50, 25090, 2), (30, 25100, 1) });

        var parsed = KiteWebSocketMarketDataProvider.TryParsePacket(packet, At);

        parsed.Should().NotBeNull();
        var (tick, depth) = (parsed!.Value.Tick, parsed.Value.Depth);

        tick.InstrumentToken.Should().Be(738561);
        tick.LastPrice.Should().Be(250.50m);
        tick.Volume.Should().Be(123456);
        tick.TimestampUtc.Should().Be(At);
        // Bid/ask come from the top of the book, not the last price.
        tick.BidPrice.Should().Be(250.40m);
        tick.AskPrice.Should().Be(250.60m);

        depth.Should().NotBeNull();
        depth!.InstrumentToken.Should().Be(738561);
        depth.LastPrice.Should().Be(250.50m);
        depth.Volume.Should().Be(123456);
        depth.TimestampUtc.Should().Be(At);

        depth.Buy.Should().HaveCount(5);
        depth.Sell.Should().HaveCount(5);
        depth.Buy[0].Should().Be(new MarketDepthLevel(250.40m, 100, 3));
        depth.Buy[4].Should().Be(new MarketDepthLevel(250.00m, 20, 1));
        depth.Sell[0].Should().Be(new MarketDepthLevel(250.60m, 120, 4));
        depth.Sell[4].Should().Be(new MarketDepthLevel(251.00m, 30, 1));
    }

    [Fact]
    public void TryParsePacket_FullMode_EmptyAskSide_FallsBackToLastPriceForAsk()
    {
        // A one-sided book (no offers): the raw depth records price 0, but the tick's ask must not report ₹0 —
        // it falls back to the last price so downstream consumers never see a zero ask.
        var packet = BuildFullPacket(
            token: 111,
            ltpPaise: 10000,       // 100.00
            volume: 0,
            bids: new[] { (10, 9990, 1), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0) },
            asks: new[] { (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0) });

        var parsed = KiteWebSocketMarketDataProvider.TryParsePacket(packet, At);

        parsed.Should().NotBeNull();
        parsed!.Value.Tick.BidPrice.Should().Be(99.90m);
        parsed.Value.Tick.AskPrice.Should().Be(100.00m);         // fallback to LTP
        parsed.Value.Depth!.Sell[0].Price.Should().Be(0m);       // raw book still reported faithfully
    }

    [Fact]
    public void TryParsePacket_QuoteMode_HasVolumeButNoDepth()
    {
        var packet = new byte[44];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 222);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 55000); // 550.00
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(16, 4), 98765); // day volume

        var parsed = KiteWebSocketMarketDataProvider.TryParsePacket(packet, At);

        parsed.Should().NotBeNull();
        parsed!.Value.Depth.Should().BeNull();
        parsed.Value.Tick.LastPrice.Should().Be(550.00m);
        parsed.Value.Tick.Volume.Should().Be(98765);
        parsed.Value.Tick.BidPrice.Should().Be(550.00m);
        parsed.Value.Tick.AskPrice.Should().Be(550.00m);
    }

    [Fact]
    public void TryParsePacket_LtpMode_NoVolumeNoDepth()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 333);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4, 4), 12345); // 123.45

        var parsed = KiteWebSocketMarketDataProvider.TryParsePacket(packet, At);

        parsed.Should().NotBeNull();
        parsed!.Value.Depth.Should().BeNull();
        parsed.Value.Tick.LastPrice.Should().Be(123.45m);
        parsed.Value.Tick.Volume.Should().Be(0);
        parsed.Value.Tick.BidPrice.Should().Be(123.45m);
        parsed.Value.Tick.AskPrice.Should().Be(123.45m);
    }

    [Fact]
    public void TryParsePacket_RuntPacket_ReturnsNull()
    {
        KiteWebSocketMarketDataProvider.TryParsePacket(new byte[4], At).Should().BeNull();
        KiteWebSocketMarketDataProvider.TryParsePacket(ReadOnlySpan<byte>.Empty, At).Should().BeNull();
    }

    private static byte[] BuildFullPacket(
        int token,
        int ltpPaise,
        int volume,
        (int qty, int pricePaise, int orders)[] bids,
        (int qty, int pricePaise, int orders)[] asks)
    {
        var packet = new byte[184];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(0, 4), token);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(4, 4), ltpPaise);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(16, 4), volume);
        WriteDepthSide(span, 64, bids);
        WriteDepthSide(span, 124, asks);
        return packet;
    }

    private static void WriteDepthSide(Span<byte> span, int start, (int qty, int pricePaise, int orders)[] levels)
    {
        for (var i = 0; i < levels.Length; i++)
        {
            var o = start + i * 12;
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(o, 4), levels[i].qty);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(o + 4, 4), levels[i].pricePaise);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(o + 8, 2), (ushort)levels[i].orders);
        }
    }
}
