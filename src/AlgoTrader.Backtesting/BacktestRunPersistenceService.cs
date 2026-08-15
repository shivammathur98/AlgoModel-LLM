namespace AlgoTrader.Backtesting;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.MarketData;

/// <summary>Immutable metadata required to reproduce and audit a backtest result later.</summary>
public sealed record BacktestRunMetadata(
    long StrategyVersionId,
    string ParametersHash,
    string Universe,
    string CostModel,
    string SlippageModel,
    string DataFingerprint,
    DateTimeOffset CreatedAtUtc,
    string? Notes = null)
{
    /// <summary>Unique idempotency key for this stored run.</summary>
    public string RunCorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Maps an in-memory engine result to the application persistence contract. The engine itself
/// remains side-effect free; callers choose whether and when a completed run is persisted.
/// </summary>
public sealed class BacktestRunPersistenceService
{
    private readonly IBacktestRunRepository _repository;

    public BacktestRunPersistenceService(IBacktestRunRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Persists a completed backtest and its immutable closed trade ledger.</summary>
    public Task<long> PersistAsync(
        BacktestRunMetadata metadata,
        BacktestRunRequest request,
        BacktestRunResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ValidateMetadata(metadata);

        var firstCandle = request.Candles.MinBy(candle => candle.TimestampUtc)!;
        var lastCandle = request.Candles.MaxBy(candle => candle.TimestampUtc)!;
        var metrics = result.Metrics;
        var record = new BacktestRunRecord(
            metadata.StrategyVersionId,
            metadata.RunCorrelationId,
            metadata.ParametersHash,
            metadata.DataFingerprint,
            metadata.Universe,
            firstCandle.TimestampUtc,
            lastCandle.TimestampUtc.AddMinutes(lastCandle.Timeframe.Minutes()),
            result.InitialCapital,
            result.FinalCapital,
            request.ExecutionModel.GetType().Name,
            metadata.CostModel,
            metadata.SlippageModel,
            metrics.TotalTrades,
            metrics.WinningTrades,
            metrics.LosingTrades,
            metrics.WinRatePercent,
            metrics.GrossPnl,
            metrics.TotalCharges,
            metrics.TotalSlippage,
            metrics.NetPnl,
            metrics.MaximumDrawdown,
            metrics.ProfitFactor,
            ToDecimal(metrics.SharpeRatio),
            ToDecimal(metrics.SortinoRatio),
            metadata.CreatedAtUtc,
            result.Trades.Select(trade => new BacktestTradeRecord(
                trade.InstrumentToken,
                trade.Symbol,
                trade.EntryTimestampUtc,
                trade.ExitTimestampUtc,
                trade.EntryPrice,
                trade.ExitPrice,
                trade.Quantity,
                trade.GrossPnl,
                trade.TotalCharges,
                trade.TotalSlippage,
                trade.NetPnl,
                (int)Math.Floor(trade.HoldingTime.TotalMinutes),
                trade.ExitReason,
                trade.TradeId)).ToList(),
            metadata.Notes);

        return _repository.AddCompletedAsync(record, cancellationToken);
    }

    /// <summary>Computes a stable SHA-256 digest over exactly the candles consumed by a run.</summary>
    public static string ComputeDataFingerprint(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var builder = new StringBuilder();
        foreach (var candle in candles.OrderBy(candle => candle.TimestampUtc).ThenBy(candle => candle.InstrumentToken))
        {
            builder.Append(candle.InstrumentToken).Append('|')
                .Append(candle.Timeframe).Append('|')
                .Append(candle.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Open.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.High.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Low.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Close.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(candle.Volume.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static decimal? ToDecimal(double? value) => value.HasValue ? Convert.ToDecimal(value.Value, CultureInfo.InvariantCulture) : null;

    private static void ValidateMetadata(BacktestRunMetadata metadata)
    {
        if (metadata.StrategyVersionId <= 0) throw new ArgumentOutOfRangeException(nameof(metadata), "Strategy version id must be positive.");
        if (metadata.ParametersHash.Length != 64 || metadata.DataFingerprint.Length != 64)
            throw new ArgumentException("ParametersHash and DataFingerprint must be SHA-256 hex digests.", nameof(metadata));
        if (string.IsNullOrWhiteSpace(metadata.Universe) || string.IsNullOrWhiteSpace(metadata.CostModel) || string.IsNullOrWhiteSpace(metadata.SlippageModel))
            throw new ArgumentException("Universe, cost model, and slippage model are required.", nameof(metadata));
    }
}
