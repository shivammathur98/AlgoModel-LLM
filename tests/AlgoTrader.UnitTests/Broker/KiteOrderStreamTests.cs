namespace AlgoTrader.UnitTests.Broker;

using AlgoTrader.Broker.Zerodha;
using AlgoTrader.Domain.Enums;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the pure Kite order-postback parser (<see cref="KiteOrderStream.TryParseFrame"/>) — the unit that
/// turns a raw ticker text frame into a <see cref="Domain.Broker.BrokerOrderUpdate"/> for the execution engine
/// (§7). It must map Kite status/fill fields faithfully, treat "average_price 0 with nothing filled" as no
/// average (not a ₹0 fill), and reject anything that is not a well-formed order postback rather than fabricating
/// an update. The socket plumbing around it is thin I/O and is left untested by convention.
/// </summary>
public sealed class KiteOrderStreamTests
{
    [Fact]
    public void TryParseFrame_MapsCompletedFill()
    {
        const string frame = """
            {"type":"order","data":{"order_id":"250823000000001","status":"COMPLETE","filled_quantity":100,"average_price":250.5,"tradingsymbol":"RELIANCE"}}
            """;

        var update = KiteOrderStream.TryParseFrame(frame);

        update.Should().NotBeNull();
        update!.BrokerOrderId.Should().Be("250823000000001");
        update.State.Should().Be(OrderState.Filled);
        update.FilledQuantity.Should().Be(100);
        update.AverageFillPrice.Should().Be(250.5m);
        update.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void TryParseFrame_MapsWorkingOrder_WithNoFill_LeavesAverageNull()
    {
        // Kite reports average_price 0 for an order that has not filled; that is "no average", not a ₹0 fill.
        const string frame = """
            {"type":"order","data":{"order_id":"OID2","status":"OPEN","filled_quantity":0,"average_price":0}}
            """;

        var update = KiteOrderStream.TryParseFrame(frame);

        update.Should().NotBeNull();
        update!.State.Should().Be(OrderState.Open);
        update.FilledQuantity.Should().Be(0);
        update.AverageFillPrice.Should().BeNull();
    }

    [Fact]
    public void TryParseFrame_MapsRejection_WithStatusMessage()
    {
        const string frame = """
            {"type":"order","data":{"order_id":"OID3","status":"REJECTED","filled_quantity":0,"average_price":0,"status_message":"Insufficient funds"}}
            """;

        var update = KiteOrderStream.TryParseFrame(frame);

        update.Should().NotBeNull();
        update!.State.Should().Be(OrderState.Rejected);
        update.StatusMessage.Should().Be("Insufficient funds");
    }

    [Fact]
    public void TryParseFrame_MapsCancellation()
    {
        const string frame = """
            {"type":"order","data":{"order_id":"OID6","status":"CANCELLED","filled_quantity":0,"average_price":0}}
            """;

        KiteOrderStream.TryParseFrame(frame)!.State.Should().Be(OrderState.Cancelled);
    }

    [Fact]
    public void TryParseFrame_MapsPartialFill_KeepsAveragePrice()
    {
        // Partially filled and still working: filled > 0, so the reported average is a real fill price to keep.
        const string frame = """
            {"type":"order","data":{"order_id":"OID5","status":"OPEN","filled_quantity":40,"average_price":251.25}}
            """;

        var update = KiteOrderStream.TryParseFrame(frame);

        update.Should().NotBeNull();
        update!.FilledQuantity.Should().Be(40);
        update.AverageFillPrice.Should().Be(251.25m);
    }

    [Fact]
    public void TryParseFrame_ReturnsNull_ForNonOrderFrame()
    {
        // The ticker socket multiplexes other message types; only "order" is a postback.
        const string frame = """{"type":"instruments_meta","data":{"count":1}}""";

        KiteOrderStream.TryParseFrame(frame).Should().BeNull();
    }

    [Fact]
    public void TryParseFrame_ReturnsNull_WhenOrderIdMissing()
    {
        const string frame = """
            {"type":"order","data":{"status":"COMPLETE","filled_quantity":100,"average_price":250.5}}
            """;

        KiteOrderStream.TryParseFrame(frame).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not valid json")]
    [InlineData("[1,2,3]")]
    public void TryParseFrame_ReturnsNull_ForBlankOrMalformedInput(string frame)
    {
        KiteOrderStream.TryParseFrame(frame).Should().BeNull();
    }
}
