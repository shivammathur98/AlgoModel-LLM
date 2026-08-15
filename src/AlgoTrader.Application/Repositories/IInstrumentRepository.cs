namespace AlgoTrader.Application.Repositories;

using AlgoTrader.Domain.Instruments;

/// <summary>Repository for instrument master data.</summary>
public interface IInstrumentRepository
{
    /// <summary>Gets an instrument by its broker token.</summary>
    Task<Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken cancellationToken = default);

    /// <summary>Gets an instrument by symbol and exchange.</summary>
    Task<Instrument?> GetBySymbolAsync(string symbol, string exchange, CancellationToken cancellationToken = default);

    /// <summary>Gets all tradable instruments for an exchange/segment.</summary>
    Task<IReadOnlyList<Instrument>> GetTradableAsync(string exchange, string segment, CancellationToken cancellationToken = default);

    /// <summary>Bulk upserts instruments.</summary>
    Task<int> UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken cancellationToken = default);
}
