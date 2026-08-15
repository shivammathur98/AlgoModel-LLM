namespace AlgoTrader.Domain.Enums;

/// <summary>
/// Fill simulation fidelity used by backtesting and paper execution (§17).
/// </summary>
public enum ExecutionModel
{
    /// <summary>Fills at the decision price with zero slippage. Research only — never for go/no-go decisions.</summary>
    Ideal,

    /// <summary>Fixed conservative slippage is applied to every fill, ignoring spread and order type.</summary>
    Conservative,

    /// <summary>Slippage plus spread-aware fills honouring order-type behaviour. Default for decision-making.</summary>
    Realistic
}
