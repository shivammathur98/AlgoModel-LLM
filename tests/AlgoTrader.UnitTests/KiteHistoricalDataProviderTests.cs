namespace AlgoTrader.UnitTests;

using System.Net;
using System.Text;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Instruments;
using AlgoTrader.MarketData.Kite;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class KiteHistoricalDataProviderTests
{
    [Fact]
    public async Task GetCandlesAsync_MapsKiteResponseAndEnforcesHalfOpenRange()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""
                { "status":"success", "data": { "candles": [
                  ["2026-01-15T09:15:00+0530", 100, 105, 99, 103, 1200],
                  ["2026-01-15T09:20:00+0530", 103, 106, 102, 104, 900]
                ] } }
                """);
        }))
        {
            BaseAddress = new Uri("https://api.kite.trade/")
        };

        var provider = CreateProvider(client);
        var from = new DateTimeOffset(2026, 1, 15, 3, 45, 0, TimeSpan.Zero);
        var to = from.AddMinutes(5);

        var candles = await provider.GetCandlesAsync(738561, Timeframe.Minute5, from, to);

        candles.Should().ContainSingle();
        candles[0].Should().BeEquivalentTo(new
        {
            InstrumentToken = 738561,
            Symbol = "RELIANCE",
            Exchange = "NSE",
            Timeframe = Timeframe.Minute5,
            TimestampUtc = from,
            Open = 100m,
            High = 105m,
            Low = 99m,
            Close = 103m,
            Volume = 1200L
        });
        captured!.RequestUri!.PathAndQuery.Should().Contain("/instruments/historical/738561/5minute");
        captured.Headers.Authorization!.Scheme.Should().Be("token");
        captured.Headers.Authorization.Parameter.Should().Be("test-key:test-token");
    }

    [Theory]
    [InlineData(Timeframe.Minute1, "minute")]
    [InlineData(Timeframe.Minute5, "5minute")]
    [InlineData(Timeframe.Minute15, "15minute")]
    [InlineData(Timeframe.Daily, "day")]
    public async Task GetCandlesAsync_UsesOfficialKiteInterval(Timeframe timeframe, string expected)
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""{ "status":"success", "data": { "candles": [] } }""");
        }))
        {
            BaseAddress = new Uri("https://api.kite.trade/")
        };

        var from = new DateTimeOffset(2026, 1, 15, 3, 45, 0, TimeSpan.Zero);
        await CreateProvider(client).GetCandlesAsync(738561, timeframe, from, from.AddDays(1));

        captured!.RequestUri!.AbsolutePath.Should().EndWith($"/instruments/historical/738561/{expected}");
    }

    private static KiteHistoricalDataProvider CreateProvider(HttpClient client) => new(
        client,
        new StubInstrumentRepository(),
        Options.Create(new BrokerSettings { ApiKey = "test-key", AccessToken = "test-token" }),
        NullLogger<KiteHistoricalDataProvider>.Instance);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class StubInstrumentRepository : IInstrumentRepository
    {
        public Task<Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<Instrument?>(new Instrument(instrumentToken, "RELIANCE", "NSE", "EQ", "Reliance Industries", 0.05m, 1));

        public Task<Instrument?> GetBySymbolAsync(string symbol, string exchange, CancellationToken cancellationToken = default) =>
            Task.FromResult<Instrument?>(null);

        public Task<IReadOnlyList<Instrument>> GetTradableAsync(string exchange, string segment, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());

        public Task<int> UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
