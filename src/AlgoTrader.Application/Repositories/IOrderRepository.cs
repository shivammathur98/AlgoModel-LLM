namespace AlgoTrader.Application.Repositories;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;

/// <summary>Repository for order persistence and querying.</summary>
public interface IOrderRepository
{
    /// <summary>Inserts a new order.</summary>
    Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing order.</summary>
    Task UpdateAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>Gets an order by its local ID.</summary>
    Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Gets an order by its broker order ID.</summary>
    Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default);

    /// <summary>Gets orders by correlation ID.</summary>
    Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>Gets all non-terminal (open) orders.</summary>
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets orders created within a time range.</summary>
    Task<IReadOnlyList<Order>> GetOrdersAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}
