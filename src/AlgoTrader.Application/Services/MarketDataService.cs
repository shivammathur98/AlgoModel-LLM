namespace AlgoTrader.Application.Services;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates market data operations: real-time tick streaming, candle aggregation,
/// historical data download, and persistence. Serves as the central orchestrator for
/// both live and historical market data flows (§7).
/// </summary>
public sealed class MarketDataService : IDisposable
{
    private readonly IMarketCandleRepository _candleRepository;
    private readonly ICandleAggregator _aggregator;
    private readonly ILogger<MarketDataService> _logger;
    private readonly IReadOnlyList<Timeframe> _timeframes;
    private ILiveMarketDataProvider? _liveFeed;
    private bool _disposed;

    public MarketDataService(
        IMarketCandleRepository candleRepository,
        ICandleAggregator aggregator,
        ILogger<MarketDataService> logger,
        IEnumerable<Timeframe>? timeframes = null)
    {
        _candleRepository = candleRepository;
        _aggregator = aggregator;
        _logger = logger;
        _timeframes = timeframes?.ToList() ?? new List<Timeframe> { Timeframe.Minute1, Timeframe.Minute5 };
    }

    /// <summary>
    /// Wires up a live market data feed: subscribes to its <see cref="ILiveMarketDataProvider.TickReceived"/>
    /// event and routes every tick through the <see cref="ICandleAggregator"/> for each configured timeframe.
    /// Closed candles are persisted to the repository.
    /// </summary>
    public void AttachLiveFeed(ILiveMarketDataProvider liveFeed)
    {
        if (_liveFeed != null) throw new InvalidOperationException("A live feed is already attached.");
        _liveFeed = liveFeed;
        _liveFeed.TickReceived += OnLiveTick;
        _logger.LogInformation("Attached live feed {Provider}", liveFeed.ProviderName);
    }

    /// <summary>Detaches the current live feed.</summary>
    public void DetachLiveFeed()
    {
        if (_liveFeed == null) return;
        _liveFeed.TickReceived -= OnLiveTick;
        _logger.LogInformation("Detached live feed {Provider}", _liveFeed.ProviderName);
        _liveFeed = null;
    }

    /// <summary>Download and persist historical candles using the given provider.</summary>
    public async Task<int> DownloadHistoricalCandlesAsync(
        IHistoricalDataProvider historicalProvider,
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Downloading historical candles from {Provider}: token={Token} {Tf} from {From} to {To}",
            historicalProvider.ProviderName, instrumentToken, timeframe, fromUtc, toUtc);

        var candles = await historicalProvider.GetCandlesAsync(
            instrumentToken, timeframe, fromUtc, toUtc, cancellationToken);

        if (candles.Count == 0)
        {
            _logger.LogWarning("No candles returned from {Provider}", historicalProvider.ProviderName);
            return 0;
        }

        var saved = await _candleRepository.SaveCandlesAsync(candles, cancellationToken);

        _logger.LogInformation(
            "Downloaded {Total} candles from {Provider}, saved {New} new candles for token {Token}",
            candles.Count, historicalProvider.ProviderName, saved, instrumentToken);

        return saved;
    }

    /// <summary>Process a single tick through the aggregator for all configured timeframes.</summary>
    public async Task ProcessTickAsync(Tick tick)
    {
        foreach (var tf in _timeframes)
        {
            var closedCandle = _aggregator.OnTick(tick, tf);
            if (closedCandle != null)
            {
                await PersistCandleAsync(closedCandle);
            }
        }
    }

    /// <summary>Get candles from the repository.</summary>
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        return await _candleRepository.GetCandlesAsync(
            instrumentToken, timeframe, fromUtc, toUtc, cancellationToken);
    }

    /// <summary>Get the latest candle timestamp for an instrument.</summary>
    public async Task<DateTimeOffset?> GetLatestCandleTimestampAsync(
        int instrumentToken,
        Timeframe timeframe,
        CancellationToken cancellationToken = default)
    {
        return await _candleRepository.GetLatestCandleTimestampAsync(
            instrumentToken, timeframe, cancellationToken);
    }

    /// <summary>Reset the aggregator for a specific instrument (e.g., at market close).</summary>
    public void ResetAggregator(int instrumentToken)
    {
        _aggregator.Reset(instrumentToken);
        _logger.LogInformation("Aggregator reset for instrument {Token}", instrumentToken);
    }

    private void OnLiveTick(object? sender, TickEventArgs e)
    {
        // Fire-and-forget: persistence errors are logged inside PersistCandleAsync.
        _ = ProcessTickAsync(e.Tick);
    }

    private async Task PersistCandleAsync(Candle candle)
    {
        try
        {
            await _candleRepository.SaveCandlesAsync(new[] { candle });
            _logger.LogDebug(
                "Persisted completed candle: {Symbol} {Tf} @ {Time} (O={O} H={H} L={L} C={C} V={V})",
                candle.Symbol, candle.Timeframe, candle.TimestampUtc,
                candle.Open, candle.High, candle.Low, candle.Close, candle.Volume);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist candle: {Symbol} {Tf} @ {Time}",
                candle.Symbol, candle.Timeframe, candle.TimestampUtc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachLiveFeed();
    }
}
