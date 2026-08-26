namespace AlgoTrader.Application.Costing;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;

/// <summary>
/// The single, centralized Zerodha NSE-equity charge calculator (§18). Every trading charge formula
/// lives here and nowhere else, so a change to how a charge is computed never touches strategy or
/// backtesting code. All rates are supplied by <see cref="CostSettings"/> — nothing is hardcoded (§30) —
/// and every amount is <see cref="decimal"/>, never floating point (§37). Each leg is priced
/// independently: pass one <see cref="CostCalculationContext"/> per executed order (buy or sell).
/// <para>
/// Charge model (per leg), matching Zerodha's published equity schedule:
/// <list type="bullet">
/// <item>Brokerage — intraday (MIS): flat / percent / min(flat, percent) per configured method; delivery (CNC): zero.</item>
/// <item>STT — intraday: SELL leg only at the intraday rate; delivery: BOTH legs at the delivery rate.</item>
/// <item>Exchange transaction charges &amp; SEBI charges — both legs, on turnover.</item>
/// <item>Stamp duty — BUY leg only; intraday and delivery use separate rates (delivery is 5× intraday).</item>
/// <item>GST — applied to brokerage + exchange transaction charges + SEBI charges.</item>
/// <item>DP charges — delivery SELL only; the returned amount is GST-inclusive.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ZerodhaEquityCostCalculator : ITradingCostCalculator
{
    private readonly CostSettings _settings;

    public ZerodhaEquityCostCalculator(CostSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public TradingCostBreakdown Calculate(CostCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(context), "Quantity must be positive.");
        if (context.Price < 0m)
            throw new ArgumentOutOfRangeException(nameof(context), "Price cannot be negative.");

        var turnover = context.Price * context.Quantity;
        var isSell = context.Side == OrderSide.Sell;
        var isBuy = context.Side == OrderSide.Buy;
        var isDelivery = context.Product == ProductType.Delivery;

        var brokerage = Round(CalculateBrokerage(turnover, isDelivery));
        var stt = Round(CalculateStt(turnover, isDelivery, isSell));
        var exchangeTransactionCharges = Round(_settings.ExchangeTransactionChargePercent * turnover);
        var sebiCharges = Round(_settings.SebiChargePercent * turnover);
        var stampDuty = Round(isBuy
            ? (isDelivery ? _settings.StampDutyPercentBuyDelivery : _settings.StampDutyPercentBuy) * turnover
            : 0m);
        var gst = Round(_settings.GstPercent * (brokerage + exchangeTransactionCharges + sebiCharges));
        var dpCharges = Round(isDelivery && isSell
            ? _settings.DpChargePerDeliverySell * (1m + _settings.DpChargeGstPercent)
            : 0m);

        return new TradingCostBreakdown(
            brokerage,
            stt,
            exchangeTransactionCharges,
            sebiCharges,
            stampDuty,
            gst,
            dpCharges,
            OtherCharges: 0m);
    }

    /// <summary>
    /// Securities Transaction Tax. Intraday (MIS) charges STT on the SELL leg only; delivery (CNC) charges
    /// STT on BOTH legs at the (higher) delivery rate. This asymmetry is why swing/delivery trades must be
    /// costed with <see cref="CostSettings.SttPercentDelivery"/>, not the intraday sell-only rate.
    /// </summary>
    private decimal CalculateStt(decimal turnover, bool isDelivery, bool isSell)
    {
        if (isDelivery) return _settings.SttPercentDelivery * turnover; // both buy and sell legs
        return isSell ? _settings.SttPercentSell * turnover : 0m;       // intraday: sell leg only
    }

    private decimal CalculateBrokerage(decimal turnover, bool isDelivery)
    {
        // Zerodha charges no brokerage on equity delivery.
        if (isDelivery) return 0m;

        var percentComponent = _settings.BrokeragePercent * turnover;
        return _settings.BrokerageMethod switch
        {
            BrokerageCalculationMethod.Flat => _settings.BrokerageFlatPerExecutedOrder,
            BrokerageCalculationMethod.Percent => percentComponent,
            BrokerageCalculationMethod.MinOfFlatAndPercent =>
                Math.Min(_settings.BrokerageFlatPerExecutedOrder, percentComponent),
            _ => throw new ArgumentOutOfRangeException(
                nameof(_settings.BrokerageMethod), _settings.BrokerageMethod, "Unknown brokerage method.")
        };
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
