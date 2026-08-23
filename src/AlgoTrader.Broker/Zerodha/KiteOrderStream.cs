namespace AlgoTrader.Broker.Zerodha;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Broker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Listens for Zerodha Kite order postbacks over the ticker WebSocket (<c>wss://ws.kite.trade</c>) and raises
/// <see cref="OrderUpdated"/> for each one (§4, §7). Kite delivers order updates as <b>text</b> frames
/// (<c>{"type":"order","data":{…}}</c>) on the same socket that streams binary ticks; because this stream never
/// subscribes to any instrument, only order postbacks arrive here. This is the async fill/cancel/reject channel
/// that <see cref="Domain.Execution.IExecutionEngine.ApplyBrokerUpdateAsync"/> consumes — without it, fills are
/// only learned at end-of-day reconciliation.
/// <para>
/// It is a thin I/O adapter owned by <see cref="ZerodhaKiteBroker"/> for the lifetime of an authenticated
/// session. The message-to-<see cref="BrokerOrderUpdate"/> mapping is factored into the pure, testable
/// <see cref="TryParseFrame"/>; the socket plumbing around it is intentionally minimal.
/// </para>
/// </summary>
public sealed class KiteOrderStream : IDisposable
{
    private readonly BrokerSettings _settings;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _cts;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    /// <summary>Raised once per parsed order postback.</summary>
    public event EventHandler<BrokerOrderUpdate>? OrderUpdated;

    /// <summary>True once the socket is connected and the receive loop is running.</summary>
    public bool IsRunning => _isRunning;

    public KiteOrderStream(BrokerSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Connects to the Kite ticker socket and begins listening. Kite pushes order updates for the authenticated
    /// user automatically — no instrument subscription is sent, so no market-data frames arrive on this socket.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning || _disposed) return;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.AccessToken))
            throw new InvalidOperationException("Broker API key and access token are required for the order stream.");

        // The URL carries the api_key and access_token as query parameters — never log it (§5).
        var url = $"wss://ws.kite.trade?api_key={_settings.ApiKey}&access_token={_settings.AccessToken}";
        await _webSocket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

        _isRunning = true;
        _logger.LogInformation("Kite order stream connected; listening for order postbacks.");
        // Fire-and-forget: the loop runs until the token is cancelled or the socket closes; Dispose tears it down.
        _ = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Maps one raw Kite ticker text frame to a <see cref="BrokerOrderUpdate"/>, or returns null when the frame
    /// is not an order postback, is malformed, or lacks an order id. Pure — the unit-tested core of this class.
    /// </summary>
    public static BrokerOrderUpdate? TryParseFrame(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Kite multiplexes several message types over the socket; only "order" carries a postback.
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "order") return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            var orderId = data.TryGetProperty("order_id", out var oid) && oid.ValueKind == JsonValueKind.String
                ? oid.GetString()
                : null;
            if (string.IsNullOrEmpty(orderId)) return null;

            // Reuse the broker's single source of truth for Kite status → OrderState.
            var state = ZerodhaKiteBroker.ParseOrderState(
                data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);

            var filled = data.TryGetProperty("filled_quantity", out var fq) && fq.ValueKind == JsonValueKind.Number
                ? fq.GetInt32()
                : 0;

            decimal? averageFillPrice = data.TryGetProperty("average_price", out var ap) && ap.ValueKind == JsonValueKind.Number
                ? ap.GetDecimal()
                : null;
            // Kite reports average_price 0 for orders with nothing filled; that is "no average", not a ₹0 fill.
            if (averageFillPrice is 0m && filled == 0) averageFillPrice = null;

            var statusMessage = data.TryGetProperty("status_message", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString()
                : null;

            return new BrokerOrderUpdate(orderId!, state, filled, averageFillPrice, DateTimeOffset.UtcNow, statusMessage);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var message = new List<byte>();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Kite order stream closed by server: {Status}.", result.CloseStatus);
                    break;
                }

                // Order postbacks are text; we never subscribe to instruments, so binary is unexpected — skip it.
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                message.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                    continue; // Reassemble a fragmented text frame before parsing.

                var text = Encoding.UTF8.GetString(message.ToArray());
                message.Clear();
                HandleTextFrame(text);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            // Disposal cancels the token and tears down the socket, which surfaces here as a WebSocket/disposed
            // exception rather than OperationCanceledException — that is an expected shutdown, not a real fault.
            if (cancellationToken.IsCancellationRequested || _disposed)
                _logger.LogDebug("Kite order stream receive loop ended during shutdown.");
            else
                _logger.LogError(ex, "Kite order stream receive loop error; order updates have stopped flowing.");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void HandleTextFrame(string text)
    {
        var update = TryParseFrame(text);
        if (update is null)
        {
            _logger.LogDebug("Kite order stream: ignored a non-order or unparseable frame.");
            return;
        }

        // Order id, state and quantity only — no account credentials or PII (§5).
        _logger.LogInformation(
            "Kite order update: {OrderId} → {State} (filled {Filled}).", update.BrokerOrderId, update.State, update.FilledQuantity);
        OrderUpdated?.Invoke(this, update);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _webSocket.Dispose();
    }
}
