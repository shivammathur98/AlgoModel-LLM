namespace AlgoTrader.Api.Endpoints;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Costing;
using AlgoTrader.Backtesting;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.MarketData.Jugaad;
using AlgoTrader.Risk;
using Microsoft.Extensions.Options;

/// <summary>
/// Backtest API endpoints. Allows triggering a backtest run via HTTP and viewing the results
/// as JSON without needing a separate CLI or test runner.
/// </summary>
public static class BacktestEndpoints
{
    /// <summary>Maps POST /api/backtest to run a strategy backtest and return the results.</summary>
    public static IEndpointRouteBuilder MapBacktestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/backtest", RunBacktestAsync)
            .WithName("RunBacktest")
            .WithDescription("Fetches historical candles and runs a backtest. Returns trades, metrics, and P&L.")
            .Accepts<BacktestRequest>("application/json")
            .Produces<BacktestResponse>(200)
            .Produces(400);

        return endpoints;
    }

    private static async Task<IResult> RunBacktestAsync(
        BacktestRequest request,
        IHistoricalDataProvider historicalProvider,
        IStrategy strategy,
        IOptions<TradingSettings> tradingSettings,
        IOptions<RiskSettings> riskSettings,
        IOptions<MarketDataSettings> marketDataSettings,
        IOptions<CostSettings> costSettings,
        ILogger<BacktestRequest> logger)
    {
        // Validate request
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return Results.BadRequest(new { error = "Symbol is required." });

        if (!DateOnly.TryParse(request.FromDate, out var fromDate))
            return Results.BadRequest(new { error = "Invalid fromDate. Use yyyy-MM-dd format." });

        if (!DateOnly.TryParse(request.ToDate, out var toDate))
            return Results.BadRequest(new { error = "Invalid toDate. Use yyyy-MM-dd format." });

        if (fromDate >= toDate)
            return Results.BadRequest(new { error = "fromDate must be before toDate." });

        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var capital = request.InitialCapital ?? tradingSettings.Value.StartingCapital;
        var risk = riskSettings.Value;

        // Determine instrument token
        int instrumentToken;
        if (string.Equals(marketDataSettings.Value.HistoricalProvider, "Jugaad", StringComparison.OrdinalIgnoreCase))
        {
            instrumentToken = JugaadHistoricalDataProvider.SyntheticToken(symbol);
        }
        else
        {
            // For Kite, the caller must provide the token
            if (request.InstrumentToken is null or 0)
                return Results.BadRequest(new { error = "InstrumentToken is required when using Kite historical provider." });
            instrumentToken = request.InstrumentToken.Value;
        }

        // Convert dates to UTC (IST 00:00 -> UTC)
        var istOffset = TimeSpan.FromHours(5.5);
        var fromUtc = new DateTimeOffset(fromDate.Year, fromDate.Month, fromDate.Day, 0, 0, 0, istOffset).ToUniversalTime();
        var toUtc = new DateTimeOffset(toDate.Year, toDate.Month, toDate.Day, 23, 59, 59, istOffset).ToUniversalTime();

        logger.LogInformation(
            "Starting backtest for {Symbol} from {From} to {To} with capital {Capital}",
            symbol, request.FromDate, request.ToDate, capital);

        // Fetch candles
        IReadOnlyList<Candle> candles;
        try
        {
            candles = await historicalProvider.GetCandlesAsync(
                instrumentToken, Timeframe.Daily, fromUtc, toUtc);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch historical candles for {Symbol}", symbol);
            return Results.BadRequest(new { error = $"Failed to fetch data: {ex.Message}" });
        }

        if (candles.Count == 0)
            return Results.Ok(new { message = "No candles returned for the given date range.", symbol, fromDate = request.FromDate, toDate = request.ToDate });

        // Build backtest request
        var costCalculator = new ZerodhaEquityCostCalculator(costSettings.Value);

        var executionModel = new CandleExecutionModel(
            new CandleExecutionSettings(ExecutionModel.Realistic));

        var positionSizer = new RiskAwarePositionSizer();

        var sizingSettings = new BacktestPositionSizingSettings(
            MaxCapitalPerTrade: risk.MaxCapitalPerTrade,
            MaxRiskPerTrade: risk.MaxRiskPerTrade,
            MaxExposurePerSymbol: risk.MaxExposurePerSymbol,
            Method: PositionSizingMethod.RiskBased);

        var backtestRequest = new BacktestRunRequest(
            Strategy: strategy,
            Candles: candles,
            InitialCapital: capital,
            PositionSizer: positionSizer,
            PositionSizing: sizingSettings,
            ExecutionModel: executionModel,
            CostCalculator: costCalculator,
            EndOfDayExitTimeIst: null, // Daily candles, no intraday exit
            MaximumHoldingTime: null,
            Product: ProductType.Delivery);

        // Run the backtest
        var engine = new BacktestEngine();
        var result = engine.Run(backtestRequest);

        logger.LogInformation(
            "Backtest complete for {Symbol}: {TradeCount} trades, Net P&L: {NetPnl:C}, Win Rate: {WinRate:P1}",
            symbol, result.Metrics.TotalTrades, result.FinalCapital - result.InitialCapital,
            result.Metrics.TotalTrades > 0 ? (double)result.Metrics.WinningTrades / result.Metrics.TotalTrades : 0);

        // Map to response
        var response = new BacktestResponse(
            Symbol: symbol,
            FromDate: request.FromDate,
            ToDate: request.ToDate,
            CandleCount: candles.Count,
            Strategy: strategy.Name,
            InitialCapital: result.InitialCapital,
            FinalCapital: result.FinalCapital,
            NetPnl: result.FinalCapital - result.InitialCapital,
            ReturnPercent: result.InitialCapital > 0
                ? Math.Round((result.FinalCapital - result.InitialCapital) / result.InitialCapital * 100, 2)
                : 0,
            TotalTrades: result.Metrics.TotalTrades,
            WinningTrades: result.Metrics.WinningTrades,
            LosingTrades: result.Metrics.LosingTrades,
            WinRate: result.Metrics.TotalTrades > 0
                ? Math.Round((double)result.Metrics.WinningTrades / result.Metrics.TotalTrades * 100, 1)
                : 0,
            MaxDrawdownPercent: result.InitialCapital > 0 ? Math.Round((double)(result.Metrics.MaximumDrawdown / result.InitialCapital) * 100, 2) : 0,
            ProfitFactor: Math.Round((double)(result.Metrics.ProfitFactor ?? 0), 2),
            GeneratedSignals: result.GeneratedSignals,
            RejectedSignals: result.RejectedSignals.Count,
            RejectionReasons: result.RejectedSignals.Select(r => $"{r.TimestampUtc:yyyy-MM-dd}: {r.Reason}").ToList(),
            Trades: result.Trades.Select(t => new TradeResponse(
                Symbol: t.Symbol,
                EntryDate: t.EntryTimestampUtc.ToString("yyyy-MM-dd"),
                ExitDate: t.ExitTimestampUtc.ToString("yyyy-MM-dd"),
                Side: "Long",
                EntryPrice: t.EntryPrice,
                ExitPrice: t.ExitPrice,
                Quantity: t.Quantity,
                GrossPnl: t.GrossPnl,
                NetPnl: t.NetPnl,
                ExitReason: t.ExitReason
            )).ToList());

        return Results.Ok(response);
    }
}

/// <summary>Request body for POST /api/backtest.</summary>
public sealed record BacktestRequest(
    string Symbol,
    string FromDate,
    string ToDate,
    decimal? InitialCapital = null,
    int? InstrumentToken = null);

/// <summary>Response body for a backtest run.</summary>
public sealed record BacktestResponse(
    string Symbol,
    string FromDate,
    string ToDate,
    int CandleCount,
    string Strategy,
    decimal InitialCapital,
    decimal FinalCapital,
    decimal NetPnl,
    decimal ReturnPercent,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    double WinRate,
    double MaxDrawdownPercent,
    double ProfitFactor,
    int GeneratedSignals,
    int RejectedSignals,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<TradeResponse> Trades);

/// <summary>Individual trade in a backtest result.</summary>
public sealed record TradeResponse(
    string Symbol,
    string EntryDate,
    string ExitDate,
    string Side,
    decimal EntryPrice,
    decimal ExitPrice,
    int Quantity,
    decimal GrossPnl,
    decimal NetPnl,
    string ExitReason);
