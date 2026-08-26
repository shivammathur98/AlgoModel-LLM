namespace AlgoTrader.UnitTests.Repositories;

using System;
using System.Threading.Tasks;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Persistence;
using AlgoTrader.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class OrderRepositoryTests
{
    private static AlgoTraderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AlgoTraderDbContext>()
            .UseSqlite("DataSource=:memory:") // Use in-memory SQLite for testing
            .Options;

        var db = new AlgoTraderDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task UpdateAsync_WithConcurrentUpdate_ThrowsDbUpdateConcurrencyException()
    {
        // Arrange
        using var db1 = CreateDbContext();
        var repo1 = new OrderRepository(db1);

        var order = new Order
        {
            InstrumentToken = 12345,
            Symbol = "TEST",
            Exchange = "NSE",
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Validity = OrderValidity.Day,
            Product = ProductType.Intraday,
            Quantity = 100,
            Price = 150.0m,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var savedOrder = await repo1.AddAsync(order);

        // Act
        // Simulate a concurrent second context reading the same order
        using var db2 = new AlgoTraderDbContext(
            new DbContextOptionsBuilder<AlgoTraderDbContext>().UseSqlite(db1.Database.GetDbConnection()).Options);
        var repo2 = new OrderRepository(db2);
        var concurrentOrder = await repo2.GetByIdAsync(savedOrder.Id);

        Assert.NotNull(concurrentOrder);

        // Context 1 updates the order
        savedOrder.State = OrderState.Open;
        await repo1.UpdateAsync(savedOrder);

        // Simulate another process writing to the DB and bumping the RowVersion behind EF's back
        db1.Database.ExecuteSqlRaw("UPDATE Orders SET RowVersion = x'010203' WHERE Id = {0}", savedOrder.Id);

        // Context 2 tries to update the now-stale order (which has the old version)
        concurrentOrder.State = OrderState.Cancelled;
        
        // Assert
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await repo2.UpdateAsync(concurrentOrder));
    }
}
