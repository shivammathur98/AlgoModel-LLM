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

/// <summary>
/// Outcome of an order placement attempt.
/// <para>
/// Three distinct outcomes — never conflate them (§20, Safety Rules #8/#9):
/// <list type="bullet">
/// <item><c>IsSuccess=true</c> — the broker accepted the order and returned <see cref="BrokerOrderId"/>.</item>
/// <item><c>IsSuccess=false, IsUncertain=false</c> — a <b>definitive</b> business rejection (e.g. a 4xx with an
/// error body): the exchange/RMS refused it and it was NOT placed. Safe to mark terminally Rejected.</item>
/// <item><c>IsSuccess=false, IsUncertain=true</c> — an <b>ambiguous</b> submission (transport failure, timeout,
/// 5xx, or a success status whose order id could not be read): the order may or may not be live at the broker.
/// The caller must NOT assume a non-fill and must NOT blindly retry — reconcile against the broker first.</item>
/// </list>
/// </para>
/// </summary>
public sealed record PlaceOrderResult(
    bool IsSuccess,
    string? BrokerOrderId = null,
    string? ErrorMessage = null,
    bool IsUncertain = false);

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
