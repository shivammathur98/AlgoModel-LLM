namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using AlgoTrader.Domain.Enums;

/// <summary>
/// Core trading configuration (section "Trading"). Live trading is triple-gated (§6, §36):
/// Mode == Live AND EnableLiveTrading == true AND the exact acknowledgement phrase.
/// </summary>
public sealed class TradingSettings
{
    public const string SectionName = "Trading";

    /// <summary>Phrase that must be configured exactly to allow live trading to start.</summary>
    public const string RequiredLiveAcknowledgement = "I-ACCEPT-LIVE-TRADING-RISK";

    /// <summary>Operating mode. Default is Backtest; the platform never defaults into Live.</summary>
    [EnumDataType(typeof(TradingMode))]
    public TradingMode Mode { get; set; } = TradingMode.Backtest;

    /// <summary>Second live gate. Must be explicitly set to true; default false.</summary>
    public bool EnableLiveTrading { get; set; }

    /// <summary>
    /// Third live gate. Must equal <see cref="RequiredLiveAcknowledgement"/> exactly.
    /// Leave empty unless you consciously accept live trading risk.
    /// </summary>
    public string LiveTradingAcknowledgement { get; set; } = string.Empty;

    /// <summary>Starting capital in INR (§10). Not all of it must ever be deployed.</summary>
    [Range(1_000.0, 1_000_000_000.0)]
    public decimal StartingCapital { get; set; } = 525_000m;
}
