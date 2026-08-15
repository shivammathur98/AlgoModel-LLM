namespace AlgoTrader.Backtesting;

using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Deterministic, candle-driven backtest engine. A strategy sees only closed candles, while a
/// resulting signal is submitted for execution on that instrument's next candle. This separation
/// prevents decisions from being filled using information unavailable at decision time.
/// </summary>
public sealed class BacktestEngine
{
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);
    private readonly BacktestPerformanceCalculator _performanceCalculator;

    public BacktestEngine(BacktestPerformanceCalculator? performanceCalculator = null)
    {
        _performanceCalculator = performanceCalculator ?? new BacktestPerformanceCalculator();
    }

    /// <summary>Runs one strategy against one timeframe's closed candles.</summary>
    public BacktestRunResult Run(BacktestRunRequest request)
    {
        ValidateRequest(request);
        var candles = request.Candles.OrderBy(candle => candle.TimestampUtc).ThenBy(candle => candle.InstrumentToken).ToList();
        var histories = new Dictionary<int, List<Candle>>();
        var positions = new Dictionary<int, SimulatedPosition>();
        var pendingSignals = new Dictionary<int, Signal>();
        var trades = new List<BacktestTrade>();
        var rejectedSignals = new List<BacktestSignalRejection>();
        var cash = request.InitialCapital;
        var generatedSignals = 0;

        foreach (var candle in candles)
        {
            var localTime = TimeOnly.FromDateTime(candle.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime);
            var localDate = DateOnly.FromDateTime(candle.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime);

            if (positions.TryGetValue(candle.InstrumentToken, out var eodPosition) && IsEndOfDay(localTime, request.EndOfDayExitTimeIst))
            {
                cash = ClosePosition(eodPosition, candle.TimestampUtc, candle.Open, "EndOfDay", request, trades, cash);
                positions.Remove(candle.InstrumentToken);
            }

            if (pendingSignals.Remove(candle.InstrumentToken, out var pendingSignal))
            {
                var scheduledDate = DateOnly.FromDateTime(pendingSignal.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime);
                if (scheduledDate != localDate)
                {
                    rejectedSignals.Add(Reject(pendingSignal, BacktestSignalRejectionReason.PendingOrderFromPreviousSession));
                }
                else if (pendingSignal.Direction == SignalDirection.LongEntry && IsEntryAllowed(candle, request.EndOfDayExitTimeIst))
                {
                    var previewFill = request.ExecutionModel.FillMarketOrder(new BacktestFillRequest(OrderSide.Buy, candle.Open, 1));
                    var size = request.PositionSizer.Calculate(new PositionSizeRequest(
                        previewFill.FillPrice,
                        pendingSignal.StopPrice ?? previewFill.FillPrice,
                        cash,
                        request.PositionSizing.MaxCapitalPerTrade,
                        request.PositionSizing.MaxRiskPerTrade,
                        request.PositionSizing.MaxExposurePerSymbol,
                        CurrentSymbolExposure: 0m,
                        Method: request.PositionSizing.Method,
                        PercentOfCapital: request.PositionSizing.PercentOfCapital));
                    if (size.IsRejected || size.Quantity <= 0)
                    {
                        rejectedSignals.Add(Reject(pendingSignal, BacktestSignalRejectionReason.InvalidPositionSize));
                    }
                    else
                    {
                        var entry = request.ExecutionModel.FillMarketOrder(new BacktestFillRequest(OrderSide.Buy, candle.Open, size.Quantity));
                        var entryCost = CalculateCosts(request.CostCalculator, candle, request.Product, OrderSide.Buy, size.Quantity, entry.FillPrice);
                        var requiredCash = entry.FillPrice * size.Quantity + entryCost;
                        if (requiredCash > cash)
                        {
                            rejectedSignals.Add(Reject(pendingSignal, BacktestSignalRejectionReason.InsufficientCapital));
                        }
                        else
                        {
                            cash -= requiredCash;
                            positions[candle.InstrumentToken] = new SimulatedPosition(
                                pendingSignal, candle.Exchange, candle.TimestampUtc, entry.FillPrice, entry.SlippageAmount, entryCost, size.Quantity);
                        }
                    }
                }
                else if (pendingSignal.Direction == SignalDirection.LongExit && positions.TryGetValue(candle.InstrumentToken, out var exitPosition))
                {
                    cash = ClosePosition(exitPosition, candle.TimestampUtc, candle.Open, "Signal", request, trades, cash);
                    positions.Remove(candle.InstrumentToken);
                }
                else if (pendingSignal.Direction == SignalDirection.LongEntry)
                {
                    rejectedSignals.Add(Reject(pendingSignal, BacktestSignalRejectionReason.EndOfDayCutoff));
                }
                else
                {
                    rejectedSignals.Add(Reject(pendingSignal, BacktestSignalRejectionReason.NoOpenPosition));
                }
            }

            if (positions.TryGetValue(candle.InstrumentToken, out var position))
            {
                var exit = DetermineProtectiveExit(position, candle, request);
                if (exit is not null)
                {
                    cash = ClosePosition(position, candle.TimestampUtc, exit.ReferencePrice, exit.Reason, request, trades, cash);
                    positions.Remove(candle.InstrumentToken);
                }
            }

            if (!histories.TryGetValue(candle.InstrumentToken, out var history))
            {
                history = [];
                histories[candle.InstrumentToken] = history;
            }
            history.Add(candle);

            var openPosition = positions.TryGetValue(candle.InstrumentToken, out var currentPosition)
                ? currentPosition.ToDomainPosition()
                : null;
            var context = new StrategyContext
            {
                Symbol = candle.Symbol,
                InstrumentToken = candle.InstrumentToken,
                Timeframe = candle.Timeframe,
                Candles = history.AsReadOnly(),
                OpenPosition = openPosition,
                CurrentTimestampUtc = candle.TimestampUtc,
                AvailableCapital = cash
            };

            var signals = request.Strategy.OnCandleClosed(context) ?? throw new InvalidOperationException("Strategies must return an empty signal list, not null.");
            generatedSignals += signals.Count;
            foreach (var signal in signals)
            {
                HandleSignal(signal, candle, request, positions, pendingSignals, rejectedSignals);
            }
        }

        foreach (var position in positions.Values)
        {
            var finalCandle = candles.Last(candle => candle.InstrumentToken == position.Signal.InstrumentToken);
            cash = ClosePosition(position, finalCandle.TimestampUtc, finalCandle.Close, "EndOfData", request, trades, cash);
        }

        var metrics = _performanceCalculator.Calculate(request.InitialCapital, trades);
        return new BacktestRunResult(trades, rejectedSignals, metrics, request.InitialCapital, cash, generatedSignals);
    }

    private static void HandleSignal(
        Signal signal,
        Candle decisionCandle,
        BacktestRunRequest request,
        IReadOnlyDictionary<int, SimulatedPosition> positions,
        IDictionary<int, Signal> pendingSignals,
        ICollection<BacktestSignalRejection> rejectedSignals)
    {
        if (signal.InstrumentToken != decisionCandle.InstrumentToken || !string.Equals(signal.Symbol, decisionCandle.Symbol, StringComparison.Ordinal))
            throw new InvalidOperationException("A strategy may only emit signals for the candle currently being evaluated.");

        if (signal.Direction == SignalDirection.LongEntry)
        {
            if (!IsEntryAllowed(decisionCandle, request.EndOfDayExitTimeIst))
            {
                rejectedSignals.Add(Reject(signal, BacktestSignalRejectionReason.EndOfDayCutoff));
            }
            else if (positions.ContainsKey(signal.InstrumentToken) || pendingSignals.ContainsKey(signal.InstrumentToken))
            {
                rejectedSignals.Add(Reject(signal, BacktestSignalRejectionReason.DuplicateEntry));
            }
            else
            {
                pendingSignals.Add(signal.InstrumentToken, signal);
            }
        }
        else if (positions.ContainsKey(signal.InstrumentToken) && !pendingSignals.ContainsKey(signal.InstrumentToken))
        {
            pendingSignals.Add(signal.InstrumentToken, signal);
        }
        else
        {
            rejectedSignals.Add(Reject(signal, BacktestSignalRejectionReason.NoOpenPosition));
        }
    }

    private static SimulatedExit? DetermineProtectiveExit(SimulatedPosition position, Candle candle, BacktestRunRequest request)
    {
        if (request.MaximumHoldingTime is { } maximumHolding && candle.TimestampUtc >= position.OpenedAtUtc + maximumHolding)
            return new SimulatedExit(candle.Open, "TimeExit");

        var hitStop = position.Signal.StopPrice is { } stop && candle.Low <= stop;
        var hitTarget = position.Signal.TargetPrice is { } target && candle.High >= target;
        if (!hitStop && !hitTarget) return null;

        if (hitStop && hitTarget)
        {
            return request.IntrabarExitPriority == IntrabarExitPriority.WorstCase
                ? new SimulatedExit(position.Signal.StopPrice!.Value, "StopLoss")
                : new SimulatedExit(position.Signal.TargetPrice!.Value, "Target");
        }

        return hitStop
            ? new SimulatedExit(position.Signal.StopPrice!.Value, "StopLoss")
            : new SimulatedExit(position.Signal.TargetPrice!.Value, "Target");
    }

    private static decimal ClosePosition(
        SimulatedPosition position,
        DateTimeOffset exitTimestampUtc,
        decimal referenceExitPrice,
        string reason,
        BacktestRunRequest request,
        ICollection<BacktestTrade> trades,
        decimal cash)
    {
        var exit = request.ExecutionModel.FillMarketOrder(new BacktestFillRequest(OrderSide.Sell, referenceExitPrice, position.Quantity));
        var exitCost = request.CostCalculator.Calculate(new CostCalculationContext(
            position.Exchange, request.Product, OrderSide.Sell, position.Quantity, exit.FillPrice)).Total;
        cash += exit.FillPrice * position.Quantity - exitCost;
        trades.Add(new BacktestTrade(
            TradeId: position.Signal.CorrelationId,
            StrategyName: position.Signal.StrategyName,
            StrategyVersion: position.Signal.StrategyVersion,
            InstrumentToken: position.Signal.InstrumentToken,
            Symbol: position.Signal.Symbol,
            EntryTimestampUtc: position.OpenedAtUtc,
            EntryPrice: position.EntryPrice,
            ExitTimestampUtc: exitTimestampUtc,
            ExitPrice: exit.FillPrice,
            Quantity: position.Quantity,
            EntryCharges: position.EntryCost,
            ExitCharges: exitCost,
            EntrySlippage: position.EntrySlippage,
            ExitSlippage: exit.SlippageAmount,
            ExitReason: reason));
        return cash;
    }

    private static decimal CalculateCosts(
        ITradingCostCalculator calculator,
        Candle candle,
        ProductType product,
        OrderSide side,
        int quantity,
        decimal price) => calculator.Calculate(new CostCalculationContext(candle.Exchange, product, side, quantity, price)).Total;

    private static bool IsEndOfDay(TimeOnly currentTimeIst, TimeOnly? exitTimeIst) => exitTimeIst is { } exit && currentTimeIst >= exit;

    private static bool IsEntryAllowed(Candle candle, TimeOnly? exitTimeIst)
    {
        if (exitTimeIst is null) return true;
        var localStart = candle.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime;
        return TimeOnly.FromDateTime(localStart.AddMinutes(candle.Timeframe.Minutes())) < exitTimeIst.Value;
    }

    private static BacktestSignalRejection Reject(Signal signal, BacktestSignalRejectionReason reason) =>
        new(signal.CorrelationId, signal.Symbol, signal.TimestampUtc, reason);

    private static void ValidateRequest(BacktestRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Strategy);
        ArgumentNullException.ThrowIfNull(request.Candles);
        ArgumentNullException.ThrowIfNull(request.PositionSizer);
        ArgumentNullException.ThrowIfNull(request.PositionSizing);
        ArgumentNullException.ThrowIfNull(request.ExecutionModel);
        ArgumentNullException.ThrowIfNull(request.CostCalculator);
        if (request.InitialCapital <= 0m) throw new ArgumentOutOfRangeException(nameof(request), "Initial capital must be positive.");
        if (request.PositionSizing.MaxCapitalPerTrade <= 0m || request.PositionSizing.MaxExposurePerSymbol <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "Backtest position-capital and exposure limits must be positive.");
        if (request.MaximumHoldingTime is { } holding && holding <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum holding time must be positive.");
        if (request.Candles.Count == 0) throw new ArgumentException("A backtest requires at least one candle.", nameof(request));

        var duplicates = request.Candles.GroupBy(candle => candle.Key).Any(group => group.Count() > 1);
        if (duplicates) throw new ArgumentException("Candles must be unique by instrument, timeframe, and timestamp.", nameof(request));
    }

    private sealed record SimulatedPosition(
        Signal Signal,
        string Exchange,
        DateTimeOffset OpenedAtUtc,
        decimal EntryPrice,
        decimal EntrySlippage,
        decimal EntryCost,
        int Quantity)
    {
        public OpenPosition ToDomainPosition() => new(
            Signal.InstrumentToken,
            Signal.Symbol,
            Signal.StrategyName,
            Quantity,
            EntryPrice,
            OpenedAtUtc,
            Signal.StopPrice,
            Signal.TargetPrice,
            Signal.CorrelationId);
    }

    private sealed record SimulatedExit(decimal ReferencePrice, string Reason);
}
