namespace AlgoTrader.UnitTests.Costing;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Costing;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using FluentAssertions;
using Xunit;

public sealed class ZerodhaEquityCostCalculatorTests
{
    [Fact]
    public void IntradaySell_ProducesExpectedComponentBreakdown()
    {
        var breakdown = Calc().Calculate(Ctx(OrderSide.Sell, ProductType.Intraday, quantity: 100, price: 100m));

        breakdown.Brokerage.Should().Be(3.00m);                 // min(₹20, 0.03% of 10,000)
        breakdown.Stt.Should().Be(2.50m);                       // 0.025% sell side
        breakdown.ExchangeTransactionCharges.Should().Be(0.30m);
        breakdown.SebiCharges.Should().Be(0.01m);
        breakdown.StampDuty.Should().Be(0m);                    // buy-side only
        breakdown.Gst.Should().Be(0.60m);                       // 18% of (3 + 0.30 + 0.01)
        breakdown.DpCharges.Should().Be(0m);                    // intraday
        breakdown.Total.Should().Be(6.41m);
    }

    [Fact]
    public void IntradayBuy_ChargesStampDutyAndNoStt()
    {
        var breakdown = Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Intraday, quantity: 100, price: 100m));

        breakdown.Stt.Should().Be(0m);
        breakdown.StampDuty.Should().Be(0.30m);                 // 0.003% buy side
        breakdown.DpCharges.Should().Be(0m);
        breakdown.Total.Should().Be(4.21m);
    }

    [Fact]
    public void DeliverySell_HasZeroBrokerageAndGstInclusiveDpCharge()
    {
        var breakdown = Calc().Calculate(Ctx(OrderSide.Sell, ProductType.Delivery, quantity: 100, price: 100m));

        breakdown.Brokerage.Should().Be(0m);                    // delivery brokerage is zero
        breakdown.Stt.Should().Be(10.00m);                      // 0.1% delivery STT (both legs)
        breakdown.DpCharges.Should().Be(15.93m);                // 13.5 * 1.18 (GST-inclusive)
        breakdown.Total.Should().Be(26.30m);                    // 10 STT + 0.30 exch + 0.01 sebi + 0.06 gst + 15.93 dp
    }

    [Fact]
    public void DeliveryBuy_HasNoDpChargeOrBrokerage()
    {
        var breakdown = Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Delivery, quantity: 100, price: 100m));

        breakdown.Brokerage.Should().Be(0m);
        breakdown.Stt.Should().Be(10.00m);                      // delivery STT applies to the buy leg too
        breakdown.DpCharges.Should().Be(0m);                    // DP applies to delivery sells only
        breakdown.StampDuty.Should().Be(1.50m);                 // 0.015% delivery buy rate (5× intraday)
        breakdown.Total.Should().Be(11.87m);                    // 10 STT + 0.30 exch + 0.01 sebi + 1.50 stamp + 0.06 gst
    }

    [Fact]
    public void StampDuty_UsesHigherDeliveryRate_ThanIntraday_OnTheBuyLeg()
    {
        // Same buy turnover (₹10,000); only the product type differs. Stamp duty must scale with it:
        // intraday 0.003% ⇒ ₹0.30, delivery 0.015% ⇒ ₹1.50 (5×). Regression guard for COST-1, where a
        // single intraday rate was applied to delivery buys, understating swing/delivery costs 5×.
        var intradayBuy = Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Intraday, quantity: 100, price: 100m));
        var deliveryBuy = Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Delivery, quantity: 100, price: 100m));

        intradayBuy.StampDuty.Should().Be(0.30m);
        deliveryBuy.StampDuty.Should().Be(1.50m);
        deliveryBuy.StampDuty.Should().Be(intradayBuy.StampDuty * 5m);
    }

    [Fact]
    public void Brokerage_IsCappedAtFlatFee_ForLargeTurnover_UnderMinMethod()
    {
        // 0.03% of 1,000,000 = ₹300, so the ₹20 flat cap wins.
        var breakdown = Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Intraday, quantity: 1_000, price: 1_000m));

        breakdown.Brokerage.Should().Be(20.00m);
    }

    [Theory]
    [InlineData(BrokerageCalculationMethod.Flat, 20.00)]
    [InlineData(BrokerageCalculationMethod.Percent, 3.00)]
    [InlineData(BrokerageCalculationMethod.MinOfFlatAndPercent, 3.00)]
    public void Brokerage_HonoursConfiguredMethod(BrokerageCalculationMethod method, double expected)
    {
        var settings = new CostSettings { BrokerageMethod = method };

        var breakdown = Calc(settings).Calculate(Ctx(OrderSide.Buy, ProductType.Intraday, quantity: 100, price: 100m));

        breakdown.Brokerage.Should().Be((decimal)expected);
    }

    [Fact]
    public void Total_EqualsSumOfAllComponents()
    {
        var breakdown = Calc().Calculate(Ctx(OrderSide.Sell, ProductType.Delivery, quantity: 250, price: 512.75m));

        var manualSum = breakdown.Brokerage + breakdown.Stt + breakdown.ExchangeTransactionCharges
                        + breakdown.SebiCharges + breakdown.StampDuty + breakdown.Gst
                        + breakdown.DpCharges + breakdown.OtherCharges;
        breakdown.Total.Should().Be(manualSum);
    }

    [Fact]
    public void NonPositiveQuantity_Throws()
    {
        var act = () => Calc().Calculate(Ctx(OrderSide.Buy, ProductType.Intraday, quantity: 0, price: 100m));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static ZerodhaEquityCostCalculator Calc(CostSettings? settings = null) => new(settings ?? new CostSettings());

    private static CostCalculationContext Ctx(OrderSide side, ProductType product, int quantity, decimal price) =>
        new("NSE", product, side, quantity, price);
}
