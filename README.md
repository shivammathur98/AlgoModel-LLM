# AlgoTrader â€” Algorithmic Trading Platform

Production-oriented algorithmic trading platform for Indian NSE equity intraday trading, built with C# / .NET 9.

## Current Status

**FEATURE-COMPLETE AT THE ENGINE LEVEL (Phases 1â€“11); Phase 12 go-live is operational, not code.** The full path â€” backtesting engine, two strategies, cost/risk/sizing engines, live WebSocket market data (ticks **and** 5-level depth), Zerodha broker integration with async order postbacks, the triple-gated order-execution engine, and the hosted paper/live trading loop with reconciliation and metrics â€” is implemented and unit-tested.

What remains is **validation and operations, not missing features**: the two strategies are unvalidated hypotheses (no walk-forward/out-of-sample study yet), and the loop has not yet been run against a live Kite feed for a full paper session or a live dry-run. See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for the full 12-phase roadmap and honest per-phase status.

## Architecture

Clean / modular-monolith. Twelve source projects; the strategy layer never touches broker types.

```
AlgoTrader.Domain          â† Core entities, value objects, interfaces (no dependencies)
AlgoTrader.Application     â† Configuration, safety, costing, observability, orchestration contracts
AlgoTrader.Persistence     â† EF Core model, migrations, repositories, seeders (SQL Server)
AlgoTrader.MarketData      â† Kite historical API + live WebSocket feed (ticks + 5-level depth)
AlgoTrader.Strategy        â† IStrategy implementations + indicators (broker-agnostic)
AlgoTrader.Backtesting     â† Deterministic candle-driven replay engine + metrics
AlgoTrader.Risk            â† Pre-trade risk engine + position sizing
AlgoTrader.Broker          â† Zerodha Kite Connect adapter + order-postback stream
AlgoTrader.Execution       â† Order execution engine (state machine, triple-gate routing)
AlgoTrader.Trading         â† Hosted trading loop, paper + live cycles, reconciliation
AlgoTrader.Infrastructure  â† System clock, cross-cutting integrations
AlgoTrader.Api             â† Composition root, health/status endpoints
```

**Dependency direction:** Domain â† Application â† everything else. Strategies depend only on Domain and Application (typed config) â€” never on broker/Zerodha classes (Â§16).

## Key Design Principles

- **Triple-gated live trading** (Â§6, Â§36): `Mode=Live` AND `EnableLiveTrading=true` AND `LiveTradingAcknowledgement="I-ACCEPT-LIVE-TRADING-RISK"`. The platform refuses to start if these are misconfigured.
- **Options validation**: All configuration sections use DataAnnotations + custom validators. Invalid config causes startup failure.
- **Deterministic time**: All business logic uses `ISystemClock` (Domain/Common/ISystemClock.cs) for testability.
- **Order state machine** (Â§25): Explicit transitions enforced in Domain/Enums/OrderEnums.cs with 40+ test cases.
- **Kill switch** (Â§15): Thread-safe emergency stop (Application/Safety/KillSwitchService.cs) with event-based notifications.
- **Historical market data**: `KiteHistoricalDataProvider` maps the Kite candle API into UTC domain candles; `HistoricalCandleBackfillService` fetches bounded windows and persists idempotently.
- **Live market data** (Â§7): `KiteWebSocketMarketDataProvider` decodes Kite's binary tick protocol, including full-mode packets carrying the **5Ã—2 market depth** book, and derives best bid/ask from the top of book.
- **Money is `decimal`, time is UTC**: all monetary values use `decimal` (never `double`); timestamps are UTC internally, IST = UTC+05:30.
- **No look-ahead** (Â§16): the backtest engine fills on the *next* candle; strategies see only closed candles available at the decision timestamp.
- **Risk before every entry** (Â§14): a stateless pre-trade risk engine vetoes risk-increasing orders; the same `RiskAwarePositionSizer` is shared by backtest, paper, and live.
- **One execution path** (Â§25): every order flows through `OrderExecutionEngine`, is persisted (even rejections, for audit), and real transmission is triple-gate re-checked at send time.

## Quick Start

```bash
# Build
dotnet build AlgoTrader.sln

# Test
dotnet test AlgoTrader.sln

# Run API
cd src/AlgoTrader.Api
dotnet run
```

API starts on `http://localhost:5080`. Endpoints:
- `GET /health` â€” liveness check
- `GET /health/ready` â€” readiness check
- `GET /api/status` â€” current mode, kill switch state, uptime

## Configuration

All settings live in `src/AlgoTrader.Api/appsettings.json`. Secrets (broker API keys, access tokens) come from environment variables or secret stores â€” never commit real credentials.

### Trading Mode (Â§6)

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
- `Research` â€” data exploration, no broker interaction
- `Backtest` â€” historical replay, no broker interaction
- `Paper` â€” live market data, simulated execution (Phase 8)
- `Live` â€” real orders (Phase 12, requires triple-gate validation)

### Risk Limits (Â§10, Â§14, Â§15)

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

### Strategy Parameters (Â§11, Â§12)

Two selectable strategies, chosen via `Strategy:Name` (both broker-agnostic, both unvalidated hypotheses):
- **MomentumBreakoutV1** â€” intraday breakout (Phase 5), shown below.
- **TrendAlignedPullbackV1** â€” 15-minute multi-session swing / delivery (trend-regime + pullback-resume entry, ATR stop capped by a max %, trend/time exits).

MomentumBreakoutV1 hypothesis (Phase 5):

```json
{
  "Strategy": {
    "Name": "MomentumBreakoutV1",
    "Version": "1.0.0",
    "Timeframe": "Minute5",
    "LookbackBars": 10,
    "VolumeMultiplier": 1.5,
    "EmaPeriod": 20,
    "UseTrendFilter": true,
    "StopLossPercent": 0.50,
    "TargetPercent": 1.00,
    "UseTrailingStop": false,
    "MaximumHoldingMinutes": 120,
    "MaxTradesPerDay": 3,
    "EntryStartTime": "09:20",
    "EntryCutoffTime": "14:30",
    "ExitTime": "15:15"
  }
}
```

**Hypothesis only** â€” these parameters are research values, not optimized constants. The strategy is unproven until backtested.

### Trading Costs (Â§18)

Zerodha NSE equity intraday charges (Phase 6):

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

**Current test coverage â€” 335 tests green** (build: 0 warnings / 0 errors on .NET 9):
- UnitTests: 321 tests (configuration/safety, persistence, candle backfill, Kite historical mapping, live WebSocket tick + depth decode, strategies, cost/risk/sizing engines, order execution, broker order stream, and the paper/live trading cycles)
- BacktestingTests: 10 tests (metrics, data splits, conservative intrabar exits, next-candle fills, end-of-day exits, slippage, and an end-to-end multi-session swing run)
- IntegrationTests: 4 tests (health/status endpoints via WebApplicationFactory)

## Project Structure

```
AlgoTrader.sln
â”œâ”€â”€ src/
â”‚   â”œâ”€â”€ AlgoTrader.Domain/              # Core entities, value objects, interfaces (no deps)
â”‚   â”‚   â”œâ”€â”€ Common/                      # Entity, ISystemClock
â”‚   â”‚   â”œâ”€â”€ Enums/                       # TradingMode, OrderState, Timeframe, etc.
â”‚   â”‚   â”œâ”€â”€ Instruments/                 # Instrument record
â”‚   â”‚   â”œâ”€â”€ MarketData/                  # Candle, Tick, MarketDepth, provider interfaces
â”‚   â”‚   â”œâ”€â”€ Orders/                      # OrderRequest, Order (state machine)
â”‚   â”‚   â”œâ”€â”€ Broker/                      # ITradingBroker, BrokerModels
â”‚   â”‚   â”œâ”€â”€ Costing/                     # ITradingCostCalculator
â”‚   â”‚   â”œâ”€â”€ Risk/                        # IRiskEngine
â”‚   â”‚   â”œâ”€â”€ Sizing/                      # IPositionSizer
â”‚   â”‚   â”œâ”€â”€ Strategy/                    # IStrategy, StrategyContext
â”‚   â”‚   â””â”€â”€ Portfolio/                   # OpenPosition
â”‚   â”œâ”€â”€ AlgoTrader.Application/          # Configuration, safety, costing, observability
â”‚   â”‚   â”œâ”€â”€ Configuration/               # Strongly-typed settings + validators
â”‚   â”‚   â”œâ”€â”€ Safety/                      # LiveTradingSafetyValidator, KillSwitchService
â”‚   â”‚   â”œâ”€â”€ Costing/                     # ZerodhaEquityCostCalculator
â”‚   â”‚   â”œâ”€â”€ Observability/               # ITradingMetrics, MeterTradingMetrics
â”‚   â”‚   â””â”€â”€ Status/                      # SystemStatus, ISystemStatusService
â”‚   â”œâ”€â”€ AlgoTrader.Persistence/          # EF Core: DbContext, migrations, repositories, seeders
â”‚   â”œâ”€â”€ AlgoTrader.MarketData/           # Kite historical provider + live WebSocket feed
â”‚   â”‚   â””â”€â”€ Kite/                        # KiteHistoricalDataProvider, KiteWebSocketMarketDataProvider
â”‚   â”œâ”€â”€ AlgoTrader.Strategy/             # MomentumBreakoutV1, TrendAlignedPullbackV1, Indicators
â”‚   â”œâ”€â”€ AlgoTrader.Backtesting/          # BacktestEngine, metrics, data splits, execution models
â”‚   â”œâ”€â”€ AlgoTrader.Risk/                 # RiskEngine, RiskAwarePositionSizer
â”‚   â”œâ”€â”€ AlgoTrader.Broker/               # Zerodha Kite adapter
â”‚   â”‚   â””â”€â”€ Zerodha/                     # ZerodhaKiteBroker, KiteOrderStream
â”‚   â”œâ”€â”€ AlgoTrader.Execution/            # OrderExecutionEngine (state machine, triple-gate routing)
â”‚   â”œâ”€â”€ AlgoTrader.Trading/              # Hosted loop, paper + live cycles, reconciliation
â”‚   â”‚   â”œâ”€â”€ TradingLoopService.cs        # Hosted service (gated, never crashes the host)
â”‚   â”‚   â”œâ”€â”€ PaperTradingCycle.cs         # PaperPortfolio + paper decision cycle
â”‚   â”‚   â””â”€â”€ LiveTradingCycle.cs          # LiveAccountView + LiveReconciler
â”‚   â”œâ”€â”€ AlgoTrader.Infrastructure/       # SystemClock, cross-cutting integrations
â”‚   â””â”€â”€ AlgoTrader.Api/                  # Composition root
â”‚       â”œâ”€â”€ Program.cs                   # Serilog, DI wiring, startup safety gate
â”‚       â”œâ”€â”€ Endpoints/                   # GET /api/status
â”‚       â””â”€â”€ appsettings.json             # Configuration template
â””â”€â”€ tests/
    â”œâ”€â”€ AlgoTrader.UnitTests/            # 294 tests
    â”œâ”€â”€ AlgoTrader.BacktestingTests/     # 7 tests
    â””â”€â”€ AlgoTrader.IntegrationTests/     # 3 tests
```

## What's Next

Phases 1â€“11 are implemented and tested. The remaining work is **validation and operations, not new engine code**:

1. **Strategy validation** â€” run both strategies through the backtest engine over real historical NSE data (walk-forward + out-of-sample). No profitability is claimed until this is done.
2. **Paper session against a live feed** â€” run the hosted loop in `Paper` mode for full sessions and reconcile the paper ledger.
3. **Live dry-run** â€” real credentials, gates *off*, zero orders sent â€” to validate connectivity, auth, and the postback stream.
4. **Staged go-live** â€” only then, the triple-gated first armed session at smallest viable size, under supervision (see `docs/GO_LIVE_CHECKLIST.md`).

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

**Last updated**: Phases 1â€“11 implemented; live market depth added; 335 tests green (2026-08-26)

