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
}
