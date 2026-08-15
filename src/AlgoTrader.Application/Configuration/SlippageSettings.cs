namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using AlgoTrader.Domain.Enums;

/// <summary>
/// Slippage and fill simulation settings (§17). Values are in basis points (1 bp = 0.01%).
/// </summary>
public sealed class SlippageSettings
{
    public const string SectionName = "Slippage";

    /// <summary>Fill model. Defaults to Realistic — never assume fills at ideal candle prices.</summary>
    public ExecutionModel Model { get; set; } = ExecutionModel.Realistic;

    /// <summary>Slippage applied against the trader on entries.</summary>
    [Range(0.0, 500.0)]
    public decimal EntrySlippageBps { get; set; } = 5m;

    /// <summary>Slippage applied against the trader on exits.</summary>
    [Range(0.0, 500.0)]
    public decimal ExitSlippageBps { get; set; } = 5m;

    /// <summary>Assumed bid/ask spread used by the realistic model.</summary>
    [Range(0.0, 500.0)]
    public decimal AssumedSpreadBps { get; set; } = 3m;

    /// <summary>Limit orders only fill at or better than the limit price.</summary>
    public bool HonorLimitPrices { get; set; } = true;
}
