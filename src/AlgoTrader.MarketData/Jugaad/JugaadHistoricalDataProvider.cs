namespace AlgoTrader.MarketData.Jugaad;

using System.Diagnostics;
using System.Globalization;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Historical data provider that fetches free daily OHLCV candles from the NSE website
/// via the jugaad-data CLI tool (<c>jdata</c>). Only supports <see cref="Timeframe.Daily"/> candles.
/// </summary>
public sealed class JugaadHistoricalDataProvider : IHistoricalDataProvider
{
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly MarketDataSettings _settings;
    private readonly ILogger<JugaadHistoricalDataProvider> _logger;

    public JugaadHistoricalDataProvider(
        IOptions<MarketDataSettings> settings,
        ILogger<JugaadHistoricalDataProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public string ProviderName => "JugaadNSE";

    public bool IsConnected => true;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (timeframe != Timeframe.Daily)
        {
            _logger.LogWarning("JugaadNSE only supports Daily candles.");
            return Array.Empty<Candle>();
        }

        if (fromUtc >= toUtc)
            throw new ArgumentException("The historical range must be non-empty and ordered.", nameof(toUtc));

        var symbol = ResolveSymbol(instrumentToken);
        if (symbol is null)
        {
            _logger.LogWarning("No symbol found for instrument token {InstrumentToken}", instrumentToken);
            return Array.Empty<Candle>();
        }

        var fromIst = fromUtc.ToOffset(IndiaStandardTimeOffset);
        var toIst = toUtc.ToOffset(IndiaStandardTimeOffset);

        var fromDate = fromIst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toDate = toIst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var tempCsvFile = Path.GetTempFileName();
        try
        {
            _logger.LogInformation("Fetching NSE data via jdata CLI for {Symbol} into {File}", symbol, tempCsvFile);
            await RunJdataCliAsync(symbol, fromDate, toDate, tempCsvFile, cancellationToken);
            var candles = await ParseCsvCandlesAsync(tempCsvFile, instrumentToken, symbol, fromUtc, toUtc, cancellationToken);
            return candles;
        }
        finally
        {
            if (File.Exists(tempCsvFile))
                File.Delete(tempCsvFile);
        }
    }

    public static int SyntheticToken(string symbol) =>
        Math.Abs(symbol.ToUpperInvariant().GetHashCode()) % 10_000_000 + 1_000_000;

    private string? ResolveSymbol(int instrumentToken)
    {
        foreach (var sym in _settings.Universe.Symbols)
        {
            if (SyntheticToken(sym) == instrumentToken)
                return sym;
        }
        return null;
    }

    private async Task RunJdataCliAsync(string symbol, string fromDate, string toDate, string outputFile, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "jdata",
            Arguments = $"stock -s {symbol} -f {fromDate} -t {toDate} -o \"{outputFile}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Failed to start 'jdata'. Ensure 'jugaad-data' is installed via pip and 'jdata' is in your PATH.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"jdata CLI failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
    }

    private async Task<List<Candle>> ParseCsvCandlesAsync(
        string csvFile,
        int instrumentToken,
        string symbol,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var result = new List<Candle>();
        var lines = await File.ReadAllLinesAsync(csvFile, cancellationToken);

        // jdata returns rows ordered descending (newest first). We need ascending.
        // Skip header at index 0.
        for (int i = lines.Length - 1; i >= 1; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            var parts = lines[i].Split(',');
            if (parts.Length < 10) continue; // Basic validation

            // Format: DATE,SERIES,OPEN,HIGH,LOW,PREV. CLOSE,LTP,CLOSE,VWAP,VOLUME,...
            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rawTimestamp))
                continue;

            // The jugaad timestamp is UTC, e.g., 2023-12-31T18:30:00Z.
            // When converted to IST, it represents 00:00 of the trading day.
            var tradeDateIst = rawTimestamp.ToOffset(IndiaStandardTimeOffset).Date;
            
            // Reconstruct as 09:15 IST (market open) for the domain model.
            var istOpen = new DateTimeOffset(
                tradeDateIst.Year, tradeDateIst.Month, tradeDateIst.Day, 9, 15, 0,
                TimeSpan.FromHours(5.5));
            var utcTimestamp = istOpen.ToUniversalTime();

            if (!decimal.TryParse(parts[2], out var open) ||
                !decimal.TryParse(parts[3], out var high) ||
                !decimal.TryParse(parts[4], out var low) ||
                !decimal.TryParse(parts[7], out var close) ||
                !long.TryParse(parts[9], out var volume))
                continue;

            if (open <= 0m || high <= 0m || low <= 0m || close <= 0m || low > high)
                continue;

            var candle = new Candle(
                instrumentToken, symbol, "NSE", Timeframe.Daily,
                utcTimestamp, open, high, low, close, volume);

            if (candle.TimestampUtc >= fromUtc && candle.TimestampUtc < toUtc)
            {
                result.Add(candle);
            }
        }

        return result.DistinctBy(c => c.TimestampUtc).OrderBy(c => c.TimestampUtc).ToList();
    }
}
