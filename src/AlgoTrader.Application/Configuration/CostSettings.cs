namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>How brokerage is computed when both a flat and a percent component exist.</summary>
public enum BrokerageCalculationMethod
{
    Flat,
    Percent,
    MinOfFlatAndPercent
}

/// <summary>
/// Centralized trading charge configuration (§18). Defaults model Zerodha NSE equity
/// intraday charges at the time of writing; they live here — as configuration — so charge
/// changes never require touching strategy or backtesting code. The formulas themselves
/// live in the single ITradingCostCalculator implementation, not scattered in the codebase.
/// </summary>
public sealed class CostSettings
{
    public const string SectionName = "Costs";

    /// <summary>Zerodha intraday equity: flat ₹20 per executed order (or percent, whichever is lower).</summary>
    [Range(0.0, 100_000.0)]
    public decimal BrokerageFlatPerExecutedOrder { get; set; } = 20m;

    /// <summary>Percent component of brokerage (0.0003 = 0.03%).</summary>
    [Range(0.0, 1.0)]
    public decimal BrokeragePercent { get; set; } = 0.0003m;

    public BrokerageCalculationMethod BrokerageMethod { get; set; } = BrokerageCalculationMethod.MinOfFlatAndPercent;

    /// <summary>STT on intraday equity sells (0.00025 = 0.025%); buy side is 0.</summary>
    [Range(0.0, 1.0)]
    public decimal SttPercentSell { get; set; } = 0.00025m;

    /// <summary>
    /// STT on equity delivery (0.001 = 0.1%), charged on BOTH the buy and the sell leg. Modelling this is
    /// essential for honest multi-day/swing (CNC) backtests: delivery pays no brokerage but this STT, on
    /// both legs, is the dominant statutory charge and dwarfs the intraday sell-only rate.
    /// </summary>
    [Range(0.0, 1.0)]
    public decimal SttPercentDelivery { get; set; } = 0.001m;

    /// <summary>NSE equity exchange transaction charges (0.0000297 = 0.00297%).</summary>
    [Range(0.0, 1.0)]
    public decimal ExchangeTransactionChargePercent { get; set; } = 0.0000297m;

    /// <summary>SEBI charges: ₹10 per crore = 0.0001% = 0.000001 as a fraction.</summary>
    [Range(0.0, 1.0)]
    public decimal SebiChargePercent { get; set; } = 0.000001m;

    /// <summary>
    /// Stamp duty on the BUY leg for INTRADAY (MIS) equity (0.00003 = 0.003% = ₹300/crore). Buyer-pays.
    /// Delivery is charged at the higher <see cref="StampDutyPercentBuyDelivery"/> rate — see that field.
    /// </summary>
    [Range(0.0, 1.0)]
    public decimal StampDutyPercentBuy { get; set; } = 0.00003m;

    /// <summary>
    /// Stamp duty on the BUY leg for DELIVERY (CNC) equity (0.00015 = 0.015% = ₹1500/crore). Buyer-pays.
    /// This is 5× the intraday rate; modelling it separately is essential for honest swing/delivery
    /// backtests, mirroring the intraday-vs-delivery split already applied to STT.
    /// </summary>
    [Range(0.0, 1.0)]
    public decimal StampDutyPercentBuyDelivery { get; set; } = 0.00015m;

    /// <summary>GST applied on brokerage + exchange charges + SEBI charges (0.18 = 18%).</summary>
    [Range(0.0, 1.0)]
    public decimal GstPercent { get; set; } = 0.18m;

    /// <summary>DP charge applies to delivery sells only; intraday sells incur none.</summary>
    [Range(0.0, 10_000.0)]
    public decimal DpChargePerDeliverySell { get; set; } = 13.5m;

    /// <summary>GST component applied on top of the DP charge.</summary>
    [Range(0.0, 1.0)]
    public decimal DpChargeGstPercent { get; set; } = 0.18m;
}
