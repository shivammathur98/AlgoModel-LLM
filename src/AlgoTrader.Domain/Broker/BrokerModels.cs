namespace AlgoTrader.Domain.Broker;

using AlgoTrader.Domain.Enums;

/// <summary>Broker account profile.</summary>
public sealed record BrokerProfile(string ClientId, string Name, IReadOnlyList<string> EnabledExchanges);

/// <summary>Margin/funds snapshot.</summary>
public sealed record BrokerFunds(decimal AvailableCash, decimal UsedMargin, decimal AvailableMargin, string Currency = "INR");

/// <summary>One holding (delivery position) reported by the broker.</summary>
public sealed record BrokerHolding(string Symbol, int InstrumentToken, int Quantity, decimal AveragePrice, decimal LastPrice);

/// <summary>One open position reported by the broker (used for reconciliation, §26).</summary>
public sealed record BrokerPositionSummary(
    string Symbol,
    int InstrumentToken,
    ProductType Product,
    OrderSide Side,
    int Quantity,
    decimal AveragePrice,
    decimal UnrealizedPnl);

/// <summary>Broker view of one order.</summary>
public sealed record BrokerOrderInfo(
    string BrokerOrderId,
    string Symbol,
    int InstrumentToken,
    OrderSide Side,
    OrderType Type,
    int Quantity,
    decimal? Price,
    OrderState State,
    int FilledQuantity,
    decimal? AverageFillPrice,
    string? StatusMessage);

/// <summary>Outcome of an order placement attempt.</summary>
public sealed record PlaceOrderResult(bool IsSuccess, string? BrokerOrderId = null, string? ErrorMessage = null);

/// <summary>Outcome of an order modification attempt.</summary>
public sealed record ModifyOrderResult(bool IsSuccess, string? ErrorMessage = null);

/// <summary>Mutable fields of an existing order that the execution engine may change.</summary>
public sealed record OrderModification(
    decimal? Price = null,
    decimal? TriggerPrice = null,
    int? Quantity = null,
    OrderValidity? Validity = null);

/// <summary>Asynchronous order status update pushed by the broker adapter.</summary>
public sealed record BrokerOrderUpdate(
    string BrokerOrderId,
    OrderState State,
    int FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset TimestampUtc,
    string? StatusMessage = null);
