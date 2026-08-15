namespace AlgoTrader.UnitTests;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Services;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class HistoricalCandleBackfillServiceTests
{
    [Fact]
    public async Task BackfillAsync_FetchesInWindowsAndPersistsOnlyNewCandles()
    {
        var from = new DateTimeOffset(2026, 1, 15, 3, 45, 0, TimeSpan.Zero);
        var provider = new RecordingHistoricalProvider();
        var repository = new DeduplicatingCandleRepository();
        var service = new HistoricalCandleBackfillService(provider, repository, NullLogger<HistoricalCandleBackfillService>.Instance);

        var result = await service.BackfillAsync(new HistoricalCandleBackfillRequest(
            738561, Timeframe.Minute5, from, from.AddMinutes(15), TimeSpan.FromMinutes(5)));

        result.Should().Be(new HistoricalCandleBackfillResult(3, 3, 3));
        provider.Requests.Should().HaveCount(3);
        provider.Requests.Select(request => request.FromUtc).Should().ContainInOrder(from, from.AddMinutes(5), from.AddMinutes(10));

        var rerun = await service.BackfillAsync(new HistoricalCandleBackfillRequest(
            738561, Timeframe.Minute5, from, from.AddMinutes(15), TimeSpan.FromMinutes(5)));
        rerun.PersistedCandles.Should().Be(0);
    }

    private sealed class RecordingHistoricalProvider : IHistoricalDataProvider
    {
        public string ProviderName => "Test";
        public bool IsConnected => true;
        public List<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> Requests { get; } = [];

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(int instrumentToken, Timeframe timeframe, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            Requests.Add((fromUtc, toUtc));
            IReadOnlyList<Candle> candles =
            [new Candle(instrumentToken, "RELIANCE", "NSE", timeframe, fromUtc, 100m, 102m, 99m, 101m, 1000L)];
            return Task.FromResult(candles);
        }
    }

    private sealed class DeduplicatingCandleRepository : IMarketCandleRepository
    {
        private readonly HashSet<CandleKey> _keys = [];

        public Task<int> SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken cancellationToken = default)
        {
            var saved = candles.Count(candle => _keys.Add(candle.Key));
            return Task.FromResult(saved);
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(int instrumentToken, Timeframe timeframe, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Candle>>(Array.Empty<Candle>());

        public Task<DateTimeOffset?> GetLatestCandleTimestampAsync(int instrumentToken, Timeframe timeframe, CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);
    }
}
