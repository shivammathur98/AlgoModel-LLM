namespace AlgoTrader.UnitTests;

using AlgoTrader.Domain.Sizing;
using AlgoTrader.Risk;
using FluentAssertions;
using Xunit;

public sealed class RiskAwarePositionSizerTests
{
    private readonly RiskAwarePositionSizer _sizer = new();

    [Fact]
    public void Calculate_RiskBasedCapsQuantityByMonetaryRiskAndCapital()
    {
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 500m,
            StopPrice: 497.5m,
            AvailableCapital: 525_000m,
            MaxCapitalPerTrade: 100_000m,
            MaxRiskPerTrade: 1_500m,
            MaxExposurePerSymbol: 100_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.Should().Be(new PositionSizeResult(
            Quantity: 200,
            Notional: 100_000m,
            RiskAmount: 500m,
            Method: PositionSizingMethod.RiskBased));
    }

    [Fact]
    public void Calculate_PercentOfCapitalHonoursPerTradeAndExposureCaps()
    {
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 95m,
            AvailableCapital: 10_000m,
            MaxCapitalPerTrade: 2_000m,
            MaxRiskPerTrade: 500m,
            MaxExposurePerSymbol: 1_500m,
            CurrentSymbolExposure: 500m,
            Method: PositionSizingMethod.PercentOfCapital,
            PercentOfCapital: 0.50m));

        result.Quantity.Should().Be(10);
        result.Notional.Should().Be(1_000m);
    }

    [Fact]
    public void Calculate_RiskBasedRejectsStopAtOrAboveEntry()
    {
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 100m,
            AvailableCapital: 10_000m,
            MaxCapitalPerTrade: 2_000m,
            MaxRiskPerTrade: 500m,
            MaxExposurePerSymbol: 2_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.IsRejected.Should().BeTrue();
        result.RejectionReason.Should().Contain("stop below entry");
    }

    // §16 adversarial boundaries. The sizer must REJECT an out-of-bounds request, never silently clamp it into a
    // trade, and must round quantity DOWN so a fill can never carry more than the sanctioned monetary risk.

    [Fact]
    public void Calculate_RiskBased_TinyStop_IsCappedByCapitalNotRisk()
    {
        // riskPerShare = 0.01 ⇒ risk budget alone would sanction 50,000 shares. Capital must bind first so the
        // position can actually be funded; the returned quantity is the (smaller) capital-limited number, and the
        // reported RiskAmount is the ACTUAL risk of that quantity, well under the ₹500 budget.
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 99.99m,
            AvailableCapital: 10_000m,
            MaxCapitalPerTrade: 10_000m,
            MaxRiskPerTrade: 500m,
            MaxExposurePerSymbol: 10_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.IsRejected.Should().BeFalse();
        result.Quantity.Should().Be(100);        // capital-bound (10,000 / 100), NOT the 50,000 risk allows
        result.Notional.Should().Be(10_000m);
        result.RiskAmount.Should().Be(1.00m);     // 100 shares × ₹0.01, far below the ₹500 budget
    }

    [Fact]
    public void Calculate_RiskBased_LargeStop_RejectsZeroQuantity()
    {
        // riskPerShare = 50 exceeds the ₹30 risk budget ⇒ 0 shares. Must reject cleanly, not submit a 0-qty order.
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 50m,
            AvailableCapital: 1_000_000m,
            MaxCapitalPerTrade: 1_000_000m,
            MaxRiskPerTrade: 30m,
            MaxExposurePerSymbol: 1_000_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.IsRejected.Should().BeTrue();
        result.Quantity.Should().Be(0);
        result.RejectionReason.Should().Contain("zero quantity");
    }

    [Fact]
    public void Calculate_InsufficientCapital_CannotFundOneShare_IsRejected()
    {
        // Capital capacity below one share's price ⇒ reject, never round up to a share we cannot pay for.
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 95m,
            AvailableCapital: 50m,
            MaxCapitalPerTrade: 1_000_000m,
            MaxRiskPerTrade: 5_000m,
            MaxExposurePerSymbol: 1_000_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.IsRejected.Should().BeTrue();
        result.Quantity.Should().Be(0);
        result.RejectionReason.Should().Contain("fund one share");
    }

    [Fact]
    public void Calculate_RiskBased_RoundsQuantityDown_NeverUpIntoMoreRisk()
    {
        // 500 / 3 = 166.67 ⇒ must floor to 166 (risk ₹498), never ceil to 167 (risk ₹501 > budget).
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 97m,
            AvailableCapital: 1_000_000m,
            MaxCapitalPerTrade: 1_000_000m,
            MaxRiskPerTrade: 500m,
            MaxExposurePerSymbol: 1_000_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        result.Quantity.Should().Be(166);
        result.RiskAmount.Should().Be(498m);       // 166 × ₹3, ≤ ₹500 budget (167 would be ₹501)
    }

    [Fact]
    public void Calculate_PercentOfCapital_OutOfRange_IsRejected()
    {
        var result = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: 100m,
            StopPrice: 95m,
            AvailableCapital: 10_000m,
            MaxCapitalPerTrade: 10_000m,
            MaxRiskPerTrade: 5_000m,
            MaxExposurePerSymbol: 10_000m,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.PercentOfCapital,
            PercentOfCapital: 1.5m));

        result.IsRejected.Should().BeTrue();
        result.RejectionReason.Should().Contain("Percent of capital");
    }
}
