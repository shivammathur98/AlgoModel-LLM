# AlgoTrader — Algorithmic Trading Platform

Production-oriented algorithmic trading platform for Indian NSE equity intraday trading, built with C# / .NET 9.

## Current Status

**PHASE 3 COMPLETE; PHASE 4 IN PROGRESS** — SQL persistence and a tested historical-data backfill pipeline are in place. The deterministic backtesting reporting/data-split foundation is next.

See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for the full 12-phase roadmap.

## Architecture

```
AlgoTrader.Domain          ← Core entities, value objects, interfaces (no dependencies)
AlgoTrader.Application     ← Configuration, safety, orchestration contracts
AlgoTrader.Infrastructure  ← System clock, external integrations
AlgoTrader.Api             ← Composition root, health/status endpoints
```

**Dependency direction:** Domain ← Application ← Infrastructure/Api

## Key Design Principles

- **Triple-gated live trading** (§6, §36): `Mode=Live` AND `EnableLiveTrading=true` AND `LiveTradingAcknowledgement="I-ACCEPT-LIVE-TRADING-RISK"`. The platform refuses to start if these are misconfigured.
- **Options validation**: All configuration sections use DataAnnotations + custom validators. Invalid config causes startup failure.
- **Deterministic time**: All business logic uses `ISystemClock` (Domain/Common/ISystemClock.cs) for testability.
- **Order state machine** (§25): Explicit transitions enforced in Domain/Enums/OrderEnums.cs with 40+ test cases.
- **Kill switch** (§15): Thread-safe emergency stop (Application/Safety/KillSwitchService.cs) with event-based notifications.
- **Historical market data**: `KiteHistoricalDataProvider` maps the Kite candle API into UTC domain candles; `HistoricalCandleBackfillService` fetches bounded windows and persists idempotently.

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
- `GET /health` — liveness check
- `GET /health/ready` — readiness check
- `GET /api/status` — current mode, kill switch state, uptime

## Configuration

All settings live in `src/AlgoTrader.Api/appsettings.json`. Secrets (broker API keys, access tokens) come from environment variables or secret stores — never commit real credentials.

### Trading Mode (§6)

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

### Risk Limits (§10, §14, §15)

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

### Broker Configuration (§5)

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

### Strategy Parameters (§11, §12)

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

**Hypothesis only** — these parameters are research values, not optimized constants. The strategy is unproven until backtested.

### Trading Costs (§18)

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

### Slippage Model (§17)

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

**Current test coverage:**
- UnitTests: 109 tests (configuration/safety, persistence, candle backfill, and Kite historical mapping)
- IntegrationTests: 3 tests (health/status endpoints via WebApplicationFactory)
- BacktestingTests: 6 tests (metrics, data splits, conservative intrabar exits, next-candle fills, end-of-day exits, and slippage)

## Project Structure

```
AlgoTrader.sln
├── src/
│   ├── AlgoTrader.Domain/              # Core entities, value objects, interfaces
│   │   ├── Common/                      # Entity, ISystemClock
│   │   ├── Enums/                       # TradingMode, OrderState, Timeframe, etc.
│   │   ├── Instruments/                 # Instrument record
│   │   ├── MarketData/                  # Candle, Tick, MarketDepth, provider interfaces
│   │   ├── Orders/                      # OrderRequest, Order (state machine)
│   │   ├── Broker/                      # ITradingBroker, BrokerModels
│   │   ├── Costing/                     # ITradingCostCalculator
│   │   ├── Risk/                        # IRiskEngine
│   │   ├── Sizing/                      # IPositionSizer
│   │   ├── Strategy/                    # IStrategy, StrategyContext
│   │   └── Portfolio/                   # OpenPosition
│   ├── AlgoTrader.Application/          # Configuration, safety, orchestration
│   │   ├── Configuration/               # 9 settings classes (Trading, Risk, Broker, etc.)
│   │   ├── Safety/                      # LiveTradingSafetyValidator, KillSwitchService
│   │   ├── Status/                      # SystemStatus, ISystemStatusService
│   │   └── DependencyInjection.cs       # AddAlgoTraderApplication()
│   ├── AlgoTrader.Infrastructure/       # System clock, future: Kite adapter, persistence
│   │   ├── SystemClock.cs
│   │   └── DependencyInjection.cs
│   └── AlgoTrader.Api/                  # Composition root
│       ├── Program.cs                   # Serilog, DI wiring, startup safety gate
│       ├── Endpoints/StatusEndpoints.cs # GET /api/status
│       ├── appsettings.json             # Configuration template
│       └── Properties/launchSettings.json
└── tests/
    ├── AlgoTrader.UnitTests/            # 92 tests
    ├── AlgoTrader.IntegrationTests/     # 3 tests
    └── AlgoTrader.BacktestingTests/     # Phase 4
```

## What's Next

### Phase 3: Historical Market Data Ingestion — complete
- Kite historical API integration; response validation; UTC conversion; bounded, idempotent persistence
- Candle storage supports 1m, 5m, 15m, and daily Kite intervals

### Phase 4: Backtesting Engine — in progress
- Complete: deterministic event loop, next-candle fills, conservative stop/target handling, time/end-of-day exits, capital curve, drawdown, Sharpe/Sortino, slippage model, and data-split boundaries
- Next batch: configurable position sizing and backtest-run persistence/replay metadata

See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for phases 5-12.

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

**Last updated**: Phase 3 complete; Phase 4 reporting foundation (2026-08-15)
