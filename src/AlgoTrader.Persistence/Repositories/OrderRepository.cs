namespace AlgoTrader.Persistence.Repositories;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>EF Core implementation of <see cref="IOrderRepository"/>.</summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly AlgoTraderDbContext _db;

    public OrderRepository(AlgoTraderDbContext db)
    {
        _db = db;
    }

    public async Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(order);
        _db.Orders.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        order.Id = entity.Id;
        return order;
    }

    public async Task UpdateAsync(Order order, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Orders.FindAsync(new object[] { order.Id }, cancellationToken);
        if (entity is null) throw new InvalidOperationException($"Order {order.Id} not found.");

        entity.BrokerOrderId = order.BrokerOrderId;
        entity.State = order.State.ToString();
        entity.FilledQuantity = order.FilledQuantity;
        entity.AverageFillPrice = order.AverageFillPrice;
        entity.RejectionReason = order.RejectionReason;
        entity.LastUpdatedAtUtc = order.LastUpdatedAtUtc;
        entity.FilledAtUtc = order.FilledAtUtc;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var e = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return e is null ? null : ToDomain(e);
    }

    public async Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default)
    {
        var e = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.BrokerOrderId == brokerOrderId, cancellationToken);
        return e is null ? null : ToDomain(e);
    }

    public async Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        return await _db.Orders.AsNoTracking()
            .Where(o => o.CorrelationId == correlationId)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => ToDomain(o))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var terminals = new[] { "Filled", "Cancelled", "Rejected", "Failed" };
        return await _db.Orders.AsNoTracking()
            .Where(o => !terminals.Contains(o.State))
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => ToDomain(o))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        return await _db.Orders.AsNoTracking()
            .Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc < toUtc)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => ToDomain(o))
            .ToListAsync(cancellationToken);
    }

    private static OrderEntity ToEntity(Order o) => new()
    {
        Id = o.Id,
        BrokerOrderId = o.BrokerOrderId,
        InstrumentToken = o.InstrumentToken,
        Symbol = o.Symbol,
        Exchange = o.Exchange,
        Side = o.Side.ToString(),
        Type = o.Type.ToString(),
        Validity = o.Validity.ToString(),
        Product = o.Product.ToString(),
        Quantity = o.Quantity,
        Price = o.Price,
        TriggerPrice = o.TriggerPrice,
        FilledQuantity = o.FilledQuantity,
        AverageFillPrice = o.AverageFillPrice,
        State = o.State.ToString(),
        RejectionReason = o.RejectionReason,
        Tag = o.Tag,
        CorrelationId = o.CorrelationId,
        StrategyName = o.StrategyName,
        CreatedAtUtc = o.CreatedAtUtc,
        LastUpdatedAtUtc = o.LastUpdatedAtUtc,
        FilledAtUtc = o.FilledAtUtc
    };

    private static Order ToDomain(OrderEntity e) => new()
    {
        Id = e.Id,
        BrokerOrderId = e.BrokerOrderId,
        InstrumentToken = e.InstrumentToken,
        Symbol = e.Symbol,
        Exchange = e.Exchange,
        Side = Enum.Parse<OrderSide>(e.Side),
        Type = Enum.Parse<OrderType>(e.Type),
        Validity = Enum.Parse<OrderValidity>(e.Validity),
        Product = Enum.Parse<ProductType>(e.Product),
        Quantity = e.Quantity,
        Price = e.Price,
        TriggerPrice = e.TriggerPrice,
        FilledQuantity = e.FilledQuantity,
        AverageFillPrice = e.AverageFillPrice,
        State = Enum.Parse<OrderState>(e.State),
        RejectionReason = e.RejectionReason,
        Tag = e.Tag,
        CorrelationId = e.CorrelationId,
        StrategyName = e.StrategyName,
        CreatedAtUtc = e.CreatedAtUtc,
        LastUpdatedAtUtc = e.LastUpdatedAtUtc,
        FilledAtUtc = e.FilledAtUtc
    };
}
