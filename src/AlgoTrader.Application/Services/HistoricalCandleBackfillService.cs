namespace AlgoTrader.Application.Services;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;

/// <summary>Input for a resumable historical candle backfill operation.</summary>
public sealed record HistoricalCandleBackfillRequest(
    int InstrumentToken,
    Timeframe Timeframe,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    TimeSpan RequestWindow);

/// <summary>Persisted outcome of a historical candle backfill operation.</summary>
public sealed record HistoricalCandleBackfillResult(int Requests, int FetchedCandles, int PersistedCandles);

/// <summary>
/// Fetches a bounded historical range in deterministic time windows and persists each response.
/// Re-running the same request is safe because the candle repository owns natural-key deduplication.
/// </summary>
public sealed class HistoricalCandleBackfillService
{
    private readonly IHistoricalDataProvider _historicalDataProvider;
    private readonly IMarketCandleRepository _candles;
    private readonly ILogger<HistoricalCandleBackfillService> _logger;

    public HistoricalCandleBackfillService(
        IHistoricalDataProvider historicalDataProvider,
        IMarketCandleRepository candles,
        ILogger<HistoricalCandleBackfillService> logger)
    {
        _historicalDataProvider = historicalDataProvider;
        _candles = candles;
        _logger = logger;
    }

    /// <summary>Executes a cancellable, idempotent backfill without silently skipping failed windows.</summary>
    public async Task<HistoricalCandleBackfillResult> BackfillAsync(
        HistoricalCandleBackfillRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var requests = 0;
        var fetched = 0;
        var persisted = 0;
        var windowStart = request.FromUtc;

        while (windowStart < request.ToUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windowEnd = Min(windowStart + request.RequestWindow, request.ToUtc);
            var candles = await _historicalDataProvider.GetCandlesAsync(
                request.InstrumentToken, request.Timeframe, windowStart, windowEnd, cancellationToken);

            fetched += candles.Count;
            persisted += await _candles.SaveCandlesAsync(candles, cancellationToken);
            requests++;
            windowStart = windowEnd;
        }

        _logger.LogInformation(
            "Historical backfill completed for token {InstrumentToken}, timeframe {Timeframe}: {Requests} requests, {Fetched} fetched, {Persisted} persisted",
            request.InstrumentToken, request.Timeframe, requests, fetched, persisted);

        return new HistoricalCandleBackfillResult(requests, fetched, persisted);
    }

    private static void Validate(HistoricalCandleBackfillRequest request)
    {
        if (request.InstrumentToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Instrument token must be positive.");
        if (request.FromUtc >= request.ToUtc)
            throw new ArgumentException("Backfill range must be non-empty and ordered.", nameof(request));
        if (request.RequestWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Request window must be positive.");
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
