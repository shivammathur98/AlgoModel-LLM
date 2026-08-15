namespace AlgoTrader.MarketData.Kite;

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Reads OHLCV bars from Kite Connect's historical-candle endpoint.
/// This adapter only maps Kite's HTTP contract to domain candles; persistence and backfill
/// orchestration remain outside the broker adapter.
/// </summary>
public sealed class KiteHistoricalDataProvider : IHistoricalDataProvider
{
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly HttpClient _httpClient;
    private readonly IInstrumentRepository _instruments;
    private readonly BrokerSettings _brokerSettings;
    private readonly ILogger<KiteHistoricalDataProvider> _logger;

    public KiteHistoricalDataProvider(
        HttpClient httpClient,
        IInstrumentRepository instruments,
        IOptions<BrokerSettings> brokerSettings,
        ILogger<KiteHistoricalDataProvider> logger)
    {
        _httpClient = httpClient;
        _instruments = instruments;
        _brokerSettings = brokerSettings.Value;
        _logger = logger;

        _httpClient.BaseAddress ??= new Uri("https://api.kite.trade/");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Remove("X-Kite-Version");
        _httpClient.DefaultRequestHeaders.Add("X-Kite-Version", "3");
    }

    /// <inheritdoc />
    public string ProviderName => "KiteHistorical";

    /// <summary>
    /// Historical requests are stateless HTTP calls. A configured API key and daily access token
    /// are the minimum prerequisite for a usable request; individual calls still validate them.
    /// </summary>
    public bool IsConnected => !string.IsNullOrWhiteSpace(_brokerSettings.ApiKey)
                               && !string.IsNullOrWhiteSpace(_brokerSettings.AccessToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (instrumentToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(instrumentToken), "An instrument token must be positive.");
        if (fromUtc >= toUtc)
            throw new ArgumentException("The historical range must be non-empty and ordered.", nameof(toUtc));
        if (string.IsNullOrWhiteSpace(_brokerSettings.ApiKey) || string.IsNullOrWhiteSpace(_brokerSettings.AccessToken))
            throw new InvalidOperationException("Kite historical data requires Broker:ApiKey and Broker:AccessToken.");

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildHistoricalUri(instrumentToken, timeframe, fromUtc, toUtc));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "token", $"{_brokerSettings.ApiKey}:{_brokerSettings.AccessToken}");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Kite historical request failed with HTTP {(int)response.StatusCode}: {ExtractErrorMessage(errorBody)}",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var candles = GetCandleArray(document.RootElement);
        var instrument = await _instruments.GetByTokenAsync(instrumentToken, cancellationToken);
        var symbol = instrument?.Symbol ?? $"TOKEN_{instrumentToken}";
        var exchange = instrument?.Exchange ?? "NSE";

        var result = new List<Candle>(candles.GetArrayLength());
        foreach (var row in candles.EnumerateArray())
        {
            if (!TryMapCandle(row, instrumentToken, symbol, exchange, timeframe, out var candle))
            {
                _logger.LogWarning("Ignoring malformed Kite historical candle for instrument token {InstrumentToken}", instrumentToken);
                continue;
            }

            // Kite's end parameter is not a domain guarantee. Enforce this interface's half-open range.
            if (candle.TimestampUtc >= fromUtc && candle.TimestampUtc < toUtc)
                result.Add(candle);
        }

        result.Sort(static (left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
        _logger.LogInformation(
            "Fetched {CandleCount} {Timeframe} candles for instrument token {InstrumentToken}",
            result.Count, timeframe, instrumentToken);
        return result;
    }

    private static string BuildHistoricalUri(int instrumentToken, Timeframe timeframe, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var from = FormatKiteTimestamp(fromUtc);
        var to = FormatKiteTimestamp(toUtc);
        return $"instruments/historical/{instrumentToken}/{MapInterval(timeframe)}?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
    }

    private static string FormatKiteTimestamp(DateTimeOffset timestampUtc) =>
        timestampUtc.ToOffset(IndiaStandardTimeOffset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static JsonElement GetCandleArray(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) &&
            !string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Kite historical request was rejected: {ExtractErrorMessage(root.GetRawText())}");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("candles", out var candles) ||
            candles.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Kite historical response did not contain data.candles.");
        }

        return candles;
    }

    private static bool TryMapCandle(
        JsonElement row,
        int instrumentToken,
        string symbol,
        string exchange,
        Timeframe timeframe,
        out Candle candle)
    {
        candle = default!;
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
            return false;

        if (!DateTimeOffset.TryParse(row[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var timestamp) ||
            !TryGetDecimal(row[1], out var open) ||
            !TryGetDecimal(row[2], out var high) ||
            !TryGetDecimal(row[3], out var low) ||
            !TryGetDecimal(row[4], out var close) ||
            !TryGetInt64(row[5], out var volume) ||
            open <= 0m || high <= 0m || low <= 0m || close <= 0m || volume < 0 || low > high)
        {
            return false;
        }

        candle = new Candle(instrumentToken, symbol, exchange, timeframe, timestamp.ToUniversalTime(), open, high, low, close, volume);
        return true;
    }

    private static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out result);
    }

    private static bool TryGetInt64(JsonElement value, out long result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result);
    }

    internal static string MapInterval(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "minute",
        Timeframe.Minute5 => "5minute",
        Timeframe.Minute15 => "15minute",
        Timeframe.Minute30 => "30minute",
        Timeframe.Minute60 => "60minute",
        Timeframe.Daily => "day",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported Kite candle interval.")
    };

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? "Kite request failed."
                : "Kite request failed.";
        }
        catch (JsonException)
        {
            return "Kite returned a non-JSON error response.";
        }
    }
}
