namespace AlgoTrader.Domain.MarketData;

/// <summary>
/// Streaming live market data source (§7). Implemented by broker adapters
/// (e.g. KiteWebSocketMarketDataProvider) and by local test feeds.
/// </summary>
public interface ILiveMarketDataProvider : IMarketDataProvider
{
    /// <summary>Raised for every tick received for a subscribed instrument.</summary>
    event EventHandler<TickEventArgs>? TickReceived;

    /// <summary>Raised when a depth snapshot is received, where the feed provides depth.</summary>
    event EventHandler<MarketDepthEventArgs>? DepthReceived;

    /// <summary>Raised when the streaming connection drops.</summary>
    event EventHandler<MarketDataDisconnectedEventArgs>? Disconnected;

    /// <summary>Opens the streaming connection.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribes to ticks for the given instrument tokens.</summary>
    Task SubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribes from ticks for the given instrument tokens.</summary>
    Task UnsubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default);

    /// <summary>Closes the streaming connection cleanly.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Event payload carrying one tick.</summary>
public sealed class TickEventArgs : EventArgs
{
    public required Tick Tick { get; init; }
}

/// <summary>Event payload carrying one depth snapshot.</summary>
public sealed class MarketDepthEventArgs : EventArgs
{
    public required MarketDepth Depth { get; init; }
}

/// <summary>Event payload describing why the feed disconnected.</summary>
public sealed class MarketDataDisconnectedEventArgs : EventArgs
{
    public string? Reason { get; init; }
    public Exception? Exception { get; init; }
}
