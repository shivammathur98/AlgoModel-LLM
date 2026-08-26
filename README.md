# AlgoTrader - Algorithmic Trading Platform

A highly structured, modular, event-driven algorithmic trading platform built in .NET 9 for the Indian equity markets (NSE via Zerodha Kite). Designed for intraday and swing equity strategies with a relentless focus on safety, execution parity, and deterministic state transitions.

> **Status:**
> **FEATURE-COMPLETE AT THE ENGINE LEVEL (Phases 1-11); Phase 12 go-live is operational, not code.** The full path - backtesting engine, two strategies, cost/risk/sizing engines, live WebSocket market data (ticks **and** 5-level depth), Zerodha broker integration with async order postbacks, the triple-gated order-execution engine, and the hosted paper/live trading loop with reconciliation and metrics - is implemented and unit-tested.

## Architecture & Layers

Following Clean Architecture principles, the platform is cleanly separated into layered projects.

```text
AlgoTrader.Domain          -> Core entities, value objects, interfaces (no dependencies)
AlgoTrader.Application     -> Configuration, safety, costing, observability, orchestration contracts
AlgoTrader.Persistence     -> EF Core model, migrations, repositories, seeders (SQL Server)
AlgoTrader.MarketData      -> Kite historical API + live WebSocket feed (ticks + 5-level depth)
AlgoTrader.Strategy        -> IStrategy implementations + indicators (broker-agnostic)
AlgoTrader.Backtesting     -> Deterministic candle-driven replay engine + metrics
AlgoTrader.Risk            -> Pre-trade risk engine + position sizing
AlgoTrader.Broker          -> Zerodha Kite Connect adapter + order-postback stream
AlgoTrader.Execution       -> Order execution engine (state machine, triple-gate routing)
AlgoTrader.Trading         -> Hosted trading loop, paper + live cycles, reconciliation
AlgoTrader.Infrastructure  -> System clock, cross-cutting integrations
AlgoTrader.Api             -> Composition root, health/status endpoints
```

**Dependency direction:** Domain <- Application <- everything else. Strategies depend only on Domain and Application (typed config) - never on broker/Zerodha classes (Sec 16).

## Key Design Principles

- **Triple-gated live trading** (Sec 6, Sec 36): `Mode=Live` AND `EnableLiveTrading=true` AND `LiveTradingAcknowledgement="I-ACCEPT-LIVE-TRADING-RISK"`. The platform refuses to start if these are misconfigured.
- **Options validation**: All configuration sections use DataAnnotations + custom validators. Invalid config causes startup failure.
- **Deterministic time**: All business logic uses `ISystemClock` (Domain/Common/ISystemClock.cs) for testability.
- **Order state machine** (Sec 25): Explicit transitions enforced in Domain/Enums/OrderEnums.cs with 40+ test cases.
- **Kill switch** (Sec 15): Thread-safe emergency stop (Application/Safety/KillSwitchService.cs) with event-based notifications.
- **Historical market data**: `KiteHistoricalDataProvider` maps the Kite candle API into UTC domain candles; `HistoricalCandleBackfillService` fetches bounded windows and persists idempotently.
- **Live market data** (Sec 7): `KiteWebSocketMarketDataProvider` decodes Kite's binary tick protocol, including full-mode packets carrying the **5x2 market depth** book, and derives best bid/ask from the top of book.
- **Money is `decimal`, time is UTC**: all monetary values use `decimal` (never `double`); timestamps are UTC internally, IST = UTC+05:30.
- **No look-ahead** (Sec 16): the backtest engine fills on the *next* candle; strategies see only closed candles available at the decision timestamp.
- **Risk before every entry** (Sec 14): a stateless pre-trade risk engine vetoes risk-increasing orders; the same `RiskAwarePositionSizer` is shared by backtest, paper, and live.
- **One execution path** (Sec 25): every order flows through `OrderExecutionEngine`, is persisted (even rejections, for audit), and real transmission is triple-gate re-checked at send time.

## Free Historical Data (`jugaad-data`)

The platform integrates with the open-source Python [`jugaad-data`](https://github.com/jugaad-py/jugaad-data) library for free historical daily candle data from the NSE, bypassing the need for a paid Kite Historical API subscription for daily swing testing.

To use it:
1. Ensure the python CLI tool is installed: `pip install jugaad-data`
2. In `appsettings.json`, set `"HistoricalProvider": "Jugaad"`
3. Define your target symbols in the `Universe.Symbols` array (e.g. `["RELIANCE", "TCS"]`).

When backtesting, the system dynamically spins up the `jdata` CLI process, generates synthetic instrument tokens, and maps the CSV directly into the engine's domain models.

## Running the API

```bash
cd src/AlgoTrader.Api
dotnet run
```

- `GET /health` - liveness check
- `GET /health/ready` - readiness check
- `GET /api/status` - current mode, kill switch state, uptime

## Configuration

All settings live in `src/AlgoTrader.Api/appsettings.json`. Secrets (broker API keys, access tokens) come from environment variables or secret stores - never commit real credentials.

### Trading Mode (Sec 6)

```json
{
  "Trading": {
    "Mode": "Backtest",
    "EnableLiveTrading": false,
    "LiveTradingAcknowledgement": "",
    "StartingCapital": 525000
  }
}
```

**Modes:**
- `Research` — data exploration, no broker interaction
- `Backtest` — historical replay, no broker interaction
- `Paper` — live market data, simulated execution (Phase 8)
- `Live` — real orders (Phase 12, requires triple-gate validation)

### Risk Limits (Sec 10, Sec 14, Sec 15)

```json
{
  "Risk": {
    "MaxCapitalPerTrade": 100000,
    "MaxCapitalUtilizationPercent": 60,
    "MaxSimultaneousPositions": 5,
    "MaxRiskPerTrade": 1500,
    "MaxDailyLoss": 5000,
    "MaxTradesPerDay": 10,
    "MaxOpenOrders": 10,
    "MaxExposurePerSymbol": 100000,
    "MarketDataStaleAfterSeconds": 30,
    "FlattenPositionsOnDailyLossBreach": true,
    "RequireManualResetAfterHalt": true
  }
}
```

### Broker Configuration (Â§5)

```json
{
  "Broker": {
    "Provider": "Zerodha",
    "Environment": "Paper",
    "ApiKey": "",
    "ApiSecret": "",
    "AccessToken": "",
    "RedirectUrl": "http://127.0.0.1:5080/kite/callback",
    "RequestTimeoutSeconds": 30,
    "MaxRetries": 3
  }
}
```

**Provide secrets via environment variables:**
```bash
export Broker__ApiKey="your_kite_api_key"
export Broker__ApiSecret="your_kite_api_secret"
export Broker__AccessToken="your_daily_access_token"
```

### Strategy Parameters (Sec 11, Sec 12)

Currently ships with two algorithmic archetypes:

- **MomentumBreakoutV1** - intraday breakout (Phase 5), shown below.
- **TrendAlignedPullbackV1** - 15-minute multi-session swing / delivery (trend-regime + pullback-resume entry, ATR stop capped by a max %, trend/time exits).

**Hypothesis only** - these parameters are research values, not optimized constants. The strategy is unproven until backtested.

```json
{
  "Strategy": {
    "ActiveStrategy": "MomentumBreakoutV1",
    "Parameters": {
      "Timeframe": "OneMinute",
      "LookbackPeriods": 20,
      "BreakoutMultiplier": 1.5,
      "StopLossAtrMultiplier": 2.0,
      "ProfitTargetAtrMultiplier": 4.0
    }
  }
}
```

### Trading Costs (Sec 18)

Models NSE equity delivery charges exactly (Zerodha schedule).

```json
{
  "Costs": {
    "BrokerageFlatPerExecutedOrder": 20,
    "BrokeragePercent": 0.0003,
    "BrokerageMethod": "MinOfFlatAndPercent",
    "SttPercentSell": 0.00025,
    "ExchangeTransactionChargePercent": 0.0000297,
    "SebiChargePercent": 0.000001,
    "StampDutyPercentBuy": 0.00003,
    "GstPercent": 0.18,
    "DpChargePerDeliverySell": 13.5,
    "DpChargeGstPercent": 0.18
  }
}
```

### Slippage Model (Â§17)

```json
{
  "Slippage": {
    "Model": "Realistic",
    "EntrySlippageBps": 5,
    "ExitSlippageBps": 5,
    "AssumedSpreadBps": 3,
    "HonorLimitPrices": true
  }
}
```

## Testing

```bash
# Run all tests
dotnet test AlgoTrader.sln

# Run with coverage
dotnet test AlgoTrader.sln --collect:"XPlat Code Coverage"

# Run specific test project
dotnet test tests/AlgoTrader.UnitTests

# Run with detailed output
dotnet test AlgoTrader.sln --verbosity normal
```

**Current test coverage - 335 tests green** (build: 0 warnings / 0 errors on .NET 9):
- UnitTests: 321 tests (configuration/safety, persistence, candle backfill, Kite historical mapping, live WebSocket tick + depth decode, strategies, cost/risk/sizing engines, order execution, broker order stream, and the paper/live trading cycles)
- BacktestingTests: 10 tests (metrics, data splits, conservative intrabar exits, next-candle fills, end-of-day exits, slippage, and an end-to-end multi-session swing run)
- IntegrationTests: 4 tests (health/status endpoints via WebApplicationFactory)

## Project Structure

```
AlgoTrader.sln
|-- src/
|   |-- AlgoTrader.Domain/              # Core entities, value objects, interfaces (no deps)
|   |   |-- Common/                      # Entity, ISystemClock
|   |   |-- Enums/                       # TradingMode, OrderState, Timeframe, etc.
|   |   |-- Instruments/                 # Instrument record
|   |   |-- MarketData/                  # Candle, Tick, MarketDepth, provider interfaces
|   |   |-- Orders/                      # OrderRequest, Order (state machine)
|   |   |-- Broker/                      # ITradingBroker, BrokerModels
|   |   |-- Costing/                     # ITradingCostCalculator
|   |   |-- Risk/                        # IRiskEngine
|   |   |-- Sizing/                      # IPositionSizer
|   |   |-- Strategy/                    # IStrategy, StrategyContext
|   |   `-- Portfolio/                   # OpenPosition
|   |-- AlgoTrader.Application/          # Configuration, safety, costing, observability
|   |   |-- Configuration/               # Strongly-typed settings + validators
|   |   |-- Safety/                      # LiveTradingSafetyValidator, KillSwitchService
|   |   |-- Costing/                     # ZerodhaEquityCostCalculator
|   |   |-- Observability/               # ITradingMetrics, MeterTradingMetrics
|   |   `-- Status/                      # SystemStatus, ISystemStatusService
|   |-- AlgoTrader.Persistence/          # EF Core: DbContext, migrations, repositories, seeders
|   |-- AlgoTrader.MarketData/           # Kite historical provider + live WebSocket feed
|   |   `-- Kite/                        # KiteHistoricalDataProvider, KiteWebSocketMarketDataProvider
|   |-- AlgoTrader.Strategy/             # MomentumBreakoutV1, TrendAlignedPullbackV1, Indicators
|   |-- AlgoTrader.Backtesting/          # BacktestEngine, metrics, data splits, execution models
|   |-- AlgoTrader.Risk/                 # RiskEngine, RiskAwarePositionSizer
|   |-- AlgoTrader.Broker/               # Zerodha Kite adapter
|   |   `-- Zerodha/                     # ZerodhaKiteBroker, KiteOrderStream
|   |-- AlgoTrader.Execution/            # OrderExecutionEngine (state machine, triple-gate routing)
|   |-- AlgoTrader.Trading/              # Hosted loop, paper + live cycles, reconciliation
|   |   |-- TradingLoopService.cs        # Hosted service (gated, never crashes the host)
|   |   |-- PaperTradingCycle.cs         # PaperPortfolio + paper decision cycle
|   |   `-- LiveTradingCycle.cs          # LiveAccountView + LiveReconciler
|   |-- AlgoTrader.Infrastructure/       # SystemClock, cross-cutting integrations
|   `-- AlgoTrader.Api/                  # Composition root
|       |-- Program.cs                   # Serilog, DI wiring, startup safety gate
|       |-- Endpoints/                   # GET /api/status
|       `-- appsettings.json             # Configuration template
`-- tests/
    |-- AlgoTrader.UnitTests/            # 294 tests
    |-- AlgoTrader.BacktestingTests/     # 7 tests
    `-- AlgoTrader.IntegrationTests/     # 3 tests
```

## What's Next

Phases 1-11 are implemented and tested. The remaining work is **validation and operations, not new engine code**:

1. **Strategy validation** - run both strategies through the backtest engine over real historical NSE data (walk-forward + out-of-sample). No profitability is claimed until this is done.
2. **Paper session against a live feed** - run the hosted loop in `Paper` mode for full sessions and reconcile the paper ledger.
3. **Live dry-run** - real credentials, gates *off*, zero orders sent - to validate connectivity, auth, and the postback stream.
4. **Staged go-live** - only then, the triple-gated first armed session at smallest viable size, under supervision (see `docs/GO_LIVE_CHECKLIST.md`).

See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for the full per-phase status.

## Safety & Risk

**This platform can lose money.** Key safeguards:
- Live trading requires explicit triple-gate opt-in
- Daily loss limits trigger automatic halt
- Kill switch for emergency stop
- Position sizing caps per-trade risk
- Reconciliation checks detect broker/platform drift

**You are responsible for:**
- Validating strategy performance before live deployment
- Monitoring live trading activity
- Setting appropriate risk limits
- Understanding Indian tax treatment (STT, GST, capital gains)

## Legal & Compliance

- **Not investment advice**: This is a research and automation tool, not a guaranteed profit system.
- **Tax obligations**: Intraday equity is speculative business income in India. Consult a CA for ITR filing.
- **Broker terms**: Comply with Zerodha's API usage policy and rate limits.
- **SEBI regulations**: Automated trading is legal; ensure your strategy doesn't violate market manipulation rules.

## Tech Stack

- **Runtime**: .NET 9 (C# 13)
- **API**: ASP.NET Core minimal APIs
- **Logging**: Serilog (structured logging, console + file sinks)
- **Configuration**: Strongly typed settings with DataAnnotations validation
- **Testing**: xUnit, FluentAssertions, Microsoft.AspNetCore.Mvc.Testing
- **Database**: SQL Server (Phase 2)
- **Broker**: Zerodha Kite Connect (Phase 9)

## License

This is a personal project. Use at your own risk.

## Contact

Questions or issues? Open a GitHub issue or contact the maintainer.

---

**Last updated**: Phases 1-11 implemented; live market depth added; 335 tests green (2026-08-26)

