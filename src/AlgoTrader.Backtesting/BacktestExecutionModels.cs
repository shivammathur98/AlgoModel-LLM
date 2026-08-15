namespace AlgoTrader.Backtesting;

using AlgoTrader.Domain.Enums;

/// <summary>Conservative policy for a candle whose range touches both a stop and target.</summary>
public enum IntrabarExitPriority
{
    /// <summary>Assume the adverse stop price was reached before the favourable target price.</summary>
    WorstCase,

    /// <summary>Assume the favourable target price was reached first. Research only.</summary>
    BestCase
}

/// <summary>Inputs for a market fill calculated from an available candle price.</summary>
public sealed record BacktestFillRequest(OrderSide Side, decimal ReferencePrice, int Quantity);

/// <summary>One simulated fill. Slippage is a positive monetary amount already represented by FillPrice.</summary>
public sealed record BacktestFill(decimal FillPrice, decimal SlippageAmount);

/// <summary>Calculates executable simulated prices without knowing strategy or portfolio state.</summary>
public interface IBacktestExecutionModel
{
    /// <summary>Returns the market fill for a known reference price.</summary>
    BacktestFill FillMarketOrder(BacktestFillRequest request);
}

/// <summary>Configuration for the deterministic candle execution model.</summary>
public sealed record CandleExecutionSettings(
    ExecutionModel Model = ExecutionModel.Realistic,
    decimal EntrySlippageBps = 0m,
    decimal ExitSlippageBps = 0m,
    decimal AssumedSpreadBps = 0m)
{
    /// <summary>Fails fast when a model would apply nonsensical price impact.</summary>
    public void Validate()
    {
        if (EntrySlippageBps < 0m || ExitSlippageBps < 0m || AssumedSpreadBps < 0m)
            throw new ArgumentOutOfRangeException(nameof(EntrySlippageBps), "Slippage and spread basis points cannot be negative.");
    }
}

/// <summary>
/// Applies configured basis-point slippage to market prices. Realistic mode also applies half of
/// the assumed bid/ask spread to each side. It does not claim to reconstruct intrabar tick order.
/// </summary>
public sealed class CandleExecutionModel : IBacktestExecutionModel
{
    private const decimal BasisPointsDivisor = 10_000m;
    private readonly CandleExecutionSettings _settings;

    public CandleExecutionModel(CandleExecutionSettings settings)
    {
        _settings = settings;
        _settings.Validate();
    }

    /// <inheritdoc />
    public BacktestFill FillMarketOrder(BacktestFillRequest request)
    {
        if (request.ReferencePrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "A fill reference price must be positive.");
        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A fill quantity must be positive.");

        var impactBps = _settings.Model switch
        {
            ExecutionModel.Ideal => 0m,
            ExecutionModel.Conservative => request.Side == OrderSide.Buy ? _settings.EntrySlippageBps : _settings.ExitSlippageBps,
            ExecutionModel.Realistic => (request.Side == OrderSide.Buy ? _settings.EntrySlippageBps : _settings.ExitSlippageBps)
                                      + _settings.AssumedSpreadBps / 2m,
            _ => throw new ArgumentOutOfRangeException()
        };
        var impact = request.ReferencePrice * impactBps / BasisPointsDivisor;
        var fillPrice = request.Side == OrderSide.Buy
            ? request.ReferencePrice + impact
            : request.ReferencePrice - impact;

        return new BacktestFill(fillPrice, impact * request.Quantity);
    }
}
