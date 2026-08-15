namespace AlgoTrader.Persistence.Repositories;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>EF Core storage for completed reproducible backtest runs.</summary>
public sealed class BacktestRunRepository : IBacktestRunRepository
{
    private readonly AlgoTraderDbContext _db;

    public BacktestRunRepository(AlgoTraderDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<long> AddCompletedAsync(BacktestRunRecord run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        Validate(run);

        var entity = new BacktestRunEntity
        {
            StrategyVersionId = run.StrategyVersionId,
            RunCorrelationId = run.RunCorrelationId,
            ParametersHash = run.ParametersHash,
            DataFingerprint = run.DataFingerprint,
            Universe = run.Universe,
            DataStartUtc = run.DataStartUtc,
            DataEndUtc = run.DataEndUtc,
            InitialCapital = run.InitialCapital,
            FinalCapital = run.FinalCapital,
            ExecutionModel = run.ExecutionModel,
            CostModel = run.CostModel,
            SlippageModel = run.SlippageModel,
            TotalTrades = run.TotalTrades,
            WinningTrades = run.WinningTrades,
            LosingTrades = run.LosingTrades,
            WinRate = run.WinRate,
            GrossPnl = run.GrossPnl,
            TotalCharges = run.TotalCharges,
            TotalSlippage = run.TotalSlippage,
            NetPnl = run.NetPnl,
            MaxDrawdown = run.MaxDrawdown,
            ProfitFactor = run.ProfitFactor,
            SharpeRatio = run.SharpeRatio,
            SortinoRatio = run.SortinoRatio,
            CreatedAtUtc = run.CreatedAtUtc,
            Status = "Completed",
            Notes = run.Notes,
            Trades = run.Trades.Select(ToEntity).ToList()
        };

        await _db.BacktestRuns.AddAsync(entity, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<BacktestRunRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.BacktestRuns
            .AsNoTracking()
            .Include(run => run.Trades)
            .SingleOrDefaultAsync(run => run.Id == id, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    private static BacktestTradeEntity ToEntity(BacktestTradeRecord trade) => new()
    {
        InstrumentToken = trade.InstrumentToken,
        Symbol = trade.Symbol,
        EntryTimestampUtc = trade.EntryTimestampUtc,
        ExitTimestampUtc = trade.ExitTimestampUtc,
        Side = "Buy",
        EntryPrice = trade.EntryPrice,
        ExitPrice = trade.ExitPrice,
        Quantity = trade.Quantity,
        GrossPnl = trade.GrossPnl,
        Charges = trade.Charges,
        Slippage = trade.Slippage,
        NetPnl = trade.NetPnl,
        HoldingMinutes = trade.HoldingMinutes,
        ExitReason = trade.ExitReason,
        CorrelationId = trade.CorrelationId
    };

    private static BacktestRunRecord ToRecord(BacktestRunEntity run) => new(
        run.StrategyVersionId,
        run.RunCorrelationId,
        run.ParametersHash,
        run.DataFingerprint,
        run.Universe,
        run.DataStartUtc,
        run.DataEndUtc,
        run.InitialCapital,
        run.FinalCapital,
        run.ExecutionModel,
        run.CostModel,
        run.SlippageModel,
        run.TotalTrades,
        run.WinningTrades,
        run.LosingTrades,
        run.WinRate,
        run.GrossPnl,
        run.TotalCharges,
        run.TotalSlippage,
        run.NetPnl,
        run.MaxDrawdown,
        run.ProfitFactor,
        run.SharpeRatio,
        run.SortinoRatio,
        run.CreatedAtUtc,
        run.Trades.OrderBy(trade => trade.ExitTimestampUtc).Select(trade => new BacktestTradeRecord(
            trade.InstrumentToken, trade.Symbol, trade.EntryTimestampUtc, trade.ExitTimestampUtc,
            trade.EntryPrice, trade.ExitPrice, trade.Quantity, trade.GrossPnl, trade.Charges,
            trade.Slippage, trade.NetPnl, trade.HoldingMinutes, trade.ExitReason, trade.CorrelationId)).ToList(),
        run.Notes)
    {
        Id = run.Id
    };

    private static void Validate(BacktestRunRecord run)
    {
        if (run.StrategyVersionId <= 0) throw new ArgumentOutOfRangeException(nameof(run), "Strategy version id must be positive.");
        if (run.InitialCapital <= 0m || run.FinalCapital < 0m) throw new ArgumentOutOfRangeException(nameof(run), "Capital values are invalid.");
        if (run.DataStartUtc >= run.DataEndUtc) throw new ArgumentException("Data range must be non-empty and ordered.", nameof(run));
        if (string.IsNullOrWhiteSpace(run.RunCorrelationId) || string.IsNullOrWhiteSpace(run.ParametersHash) || string.IsNullOrWhiteSpace(run.DataFingerprint))
            throw new ArgumentException("Run correlation id and reproducibility hashes are required.", nameof(run));
        if (run.Trades.Count != run.TotalTrades) throw new ArgumentException("Total trade count must match the persisted trade ledger.", nameof(run));
    }
}
