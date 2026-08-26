namespace AlgoTrader.Domain.Broker;

using AlgoTrader.Domain.Orders;

/// <summary>
/// Broker-agnostic trading contract (§4). Strategies, risk and execution engines only ever
/// see this interface; concrete adapters (ZerodhaKiteBroker, later FyersBroker/DhanBroker)
/// live in AlgoTrader.Broker. Strategy code must never reference broker-specific types.
/// Streaming market data is exposed separately through
/// <see cref="AlgoTrader.Domain.MarketData.ILiveMarketDataProvider"/> (§5 separation of
/// authentication, order management and market-data management).
/// </summary>
public interface ITradingBroker
{
    /// <summary>Stable provider identifier, e.g. "Zerodha".</summary>
    string ProviderName { get; }

    /// <summary>True when a valid broker session/access token is available.</summary>
    bool IsAuthenticated { get; }

    /// <summary>True when the order stream is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Establishes or refreshes the broker session.</summary>
    Task AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>Account profile.</summary>
    Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Funds/margin snapshot.</summary>
    Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default);

    /// <summary>Current holdings (delivery).</summary>
    Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Current broker-side positions.</summary>
    Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default);

    /// <summary>All orders for the current trading day.</summary>
    Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>Status of a single broker order.</summary>
    Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default);

    /// <summary>Places a new order. Never throws for business rejections; those surface in the result.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing order.</summary>
    Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default);

    /// <summary>Cancels an existing order.</summary>
    Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default);

    /// <summary>Raised for asynchronous order status updates (order subscription, §4).</summary>
    event EventHandler<BrokerOrderUpdate>? OrderUpdated;

    /// <summary>Raised if the asynchronous order postback stream drops.</summary>
    event EventHandler<EventArgs>? StreamDisconnected;
}
