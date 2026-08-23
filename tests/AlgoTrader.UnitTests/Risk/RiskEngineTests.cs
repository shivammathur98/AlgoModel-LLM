namespace AlgoTrader.UnitTests.Risk;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Risk;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies the pre-trade risk authority (§14, §15): system-integrity gates block everything (entries and
/// exits alike), while risk-budget / market-condition gates block only risk-increasing entries and buys —
/// an exit / sell that reduces exposure must never be trapped by a budget limit.
/// </summary>
public sealed class RiskEngineTests
{
    // 10:00 IST (04:30 UTC) is inside the 09:15–15:30 session; 08:30 IST (03:00 UTC) is before the open.
    private static readonly DateTimeOffset InsideHoursUtc = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OutsideHoursUtc = new(2026, 8, 24, 3, 0, 0, TimeSpan.Zero);

    // ---- Signal: happy path ------------------------------------------------

    [Fact]
    public async Task EntrySignal_WhenAllClear_IsApproved()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx());

        decision.IsApproved.Should().BeTrue();
        decision.Reason.Should().Be(RiskRejectionReason.None);
    }

    // ---- Signal: system-integrity gates (apply to entries AND exits) -------

    [Fact]
    public async Task EntrySignal_WhenKillSwitchEngaged_IsRejected()
    {
        var kill = new FakeKillSwitch();
        kill.Engage("manual test halt");

        var decision = await Engine(kill).EvaluateSignalAsync(EntrySignal(), Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.KillSwitchActive);
    }

    [Fact]
    public async Task ExitSignal_WhenKillSwitchEngaged_IsAlsoRejected()
    {
        // Exits reduce risk, but a halted platform must send nothing through the normal path.
        var kill = new FakeKillSwitch();
        kill.Engage("manual test halt");

        var decision = await Engine(kill).EvaluateSignalAsync(ExitSignal(), Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.KillSwitchActive);
    }

    [Fact]
    public async Task EntrySignal_WhenBrokerDisconnected_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(isBrokerConnected: false));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.BrokerDisconnected);
    }

    [Fact]
    public async Task NullSignal_IsRejectedAsInvalidRequest()
    {
        var decision = await Engine().EvaluateSignalAsync(null!, Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.InvalidRequest);
    }

    // ---- Signal: budget / market-condition gates (entries only) ------------

    [Fact]
    public async Task ExitSignal_WhenDailyLossBreached_IsStillApproved()
    {
        // The whole point: a daily-loss halt must not prevent closing the losing position.
        var decision = await Engine().EvaluateSignalAsync(ExitSignal(), Ctx(realizedPnlToday: -9_999m));

        decision.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task EntrySignal_WhenDailyLossBreached_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(realizedPnlToday: -5_000m));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.MaxDailyLossBreached);
    }

    [Fact]
    public async Task EntrySignal_WhenTradesPerDayReached_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(tradesToday: 10));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.MaxTradesPerDayBreached);
    }

    [Fact]
    public async Task EntrySignal_WhenMaxPositionsReached_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(openPositionCount: 5));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.MaxSimultaneousPositionsBreached);
    }

    [Fact]
    public async Task EntrySignal_WhenSymbolAlreadyOpen_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(
            EntrySignal("INFY"), Ctx(symbolsWithOpenPositions: new HashSet<string> { "INFY" }));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.SymbolAlreadyOpen);
    }

    [Fact]
    public async Task EntrySignal_WhenOutsideTradingHours_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(timestampUtc: OutsideHoursUtc));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.OutsideTradingHours);
    }

    [Fact]
    public async Task EntrySignal_WhenMarketDataStale_IsRejected()
    {
        var decision = await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(isMarketDataStale: true));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.MarketDataStale);
    }

    // ---- Order ------------------------------------------------------------

    [Fact]
    public async Task BuyOrder_WithinLimits_IsApproved()
    {
        var decision = await Engine().EvaluateOrderAsync(BuyOrder(quantity: 1, price: 50_000m), Ctx());

        decision.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task MarketBuyOrder_WithNoPrice_SkipsMonetaryChecks_AndIsApproved()
    {
        var order = new OrderRequest(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Market, 1, ProductType.Delivery);

        var decision = await Engine().EvaluateOrderAsync(order, Ctx(availableCapital: 1m));

        decision.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task BuyOrder_ExceedingMaxCapitalPerTrade_IsRejected()
    {
        var decision = await Engine().EvaluateOrderAsync(BuyOrder(quantity: 1, price: 100_001m), Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.MaxCapitalUtilizationBreached);
    }

    [Fact]
    public async Task BuyOrder_ExceedingAvailableCapital_IsRejectedAsInsufficientFunds()
    {
        var decision = await Engine().EvaluateOrderAsync(
            BuyOrder(quantity: 1, price: 50_000m), Ctx(availableCapital: 10_000m));

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.InsufficientFunds);
    }

    [Fact]
    public async Task SellOrder_WhenDailyLossBreached_IsStillApproved()
    {
        var order = new OrderRequest(738561, "INFY", "NSE", OrderSide.Sell, OrderType.Limit, 1, ProductType.Delivery, Price: 50_000m);

        var decision = await Engine().EvaluateOrderAsync(order, Ctx(realizedPnlToday: -9_999m, availableCapital: 0m));

        decision.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task Order_WithNonPositiveQuantity_IsRejectedAsInvalidRequest()
    {
        var order = new OrderRequest(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, 0, ProductType.Delivery, Price: 100m);

        var decision = await Engine().EvaluateOrderAsync(order, Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.InvalidRequest);
    }

    [Fact]
    public async Task BuyOrder_WhenKillSwitchEngaged_IsRejected()
    {
        var kill = new FakeKillSwitch();
        kill.Engage("manual test halt");

        var decision = await Engine(kill).EvaluateOrderAsync(BuyOrder(quantity: 1, price: 100m), Ctx());

        decision.IsApproved.Should().BeFalse();
        decision.Reason.Should().Be(RiskRejectionReason.KillSwitchActive);
    }

    [Fact]
    public async Task EvaluateSignalAsync_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Engine().EvaluateSignalAsync(EntrySignal(), Ctx(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Helpers ----------------------------------------------------------

    private static RiskEngine Engine(IKillSwitch? killSwitch = null, RiskSettings? settings = null) =>
        new(settings ?? new RiskSettings(), killSwitch ?? new FakeKillSwitch(), NullLogger<RiskEngine>.Instance);

    private static RiskEvaluationContext Ctx(
        DateTimeOffset? timestampUtc = null,
        decimal availableCapital = 1_000_000m,
        decimal realizedPnlToday = 0m,
        int tradesToday = 0,
        int openPositionCount = 0,
        int openOrderCount = 0,
        IReadOnlySet<string>? symbolsWithOpenPositions = null,
        bool isMarketDataStale = false,
        bool isBrokerConnected = true) =>
        new(timestampUtc ?? InsideHoursUtc, availableCapital, realizedPnlToday, tradesToday,
            openPositionCount, openOrderCount, symbolsWithOpenPositions ?? new HashSet<string>(),
            isMarketDataStale, isBrokerConnected);

    private static Signal EntrySignal(string symbol = "INFY") =>
        new("TrendAlignedPullbackV1", "1.0.0", 738561, symbol, SignalDirection.LongEntry, InsideHoursUtc,
            EntryPrice: 100m, StopPrice: 99m, TargetPrice: 101m);

    private static Signal ExitSignal(string symbol = "INFY") =>
        new("TrendAlignedPullbackV1", "1.0.0", 738561, symbol, SignalDirection.LongExit, InsideHoursUtc);

    private static OrderRequest BuyOrder(int quantity, decimal price) =>
        new(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, quantity, ProductType.Delivery, Price: price);

    /// <summary>Minimal in-test kill switch; raises <see cref="StateChanged"/> so no member is left unused.</summary>
    private sealed class FakeKillSwitch : IKillSwitch
    {
        public KillSwitchState State => IsEngaged ? KillSwitchState.Engaged : KillSwitchState.Disengaged;
        public bool IsEngaged { get; private set; }
        public string? Reason { get; private set; }

        public void Engage(string reason, string initiatedBy = "system")
        {
            IsEngaged = true;
            Reason = reason;
            StateChanged?.Invoke(this, new KillSwitchEventArgs { State = KillSwitchState.Engaged, Reason = reason, InitiatedBy = initiatedBy });
        }

        public void Reset(string initiatedBy = "operator")
        {
            IsEngaged = false;
            Reason = null;
            StateChanged?.Invoke(this, new KillSwitchEventArgs { State = KillSwitchState.Disengaged, Reason = "reset", InitiatedBy = initiatedBy });
        }

        public event EventHandler<KillSwitchEventArgs>? StateChanged;
    }
}
