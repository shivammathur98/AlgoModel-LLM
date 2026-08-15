namespace AlgoTrader.MarketData.Kite;

using System.Buffers.Binary;
using System.Net.WebSockets;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Kite Connect WebSocket market data provider (§7).
/// Streams real-time ticks from Kite's binary WebSocket protocol and raises
/// <see cref="ILiveMarketDataProvider.TickReceived"/> events for each parsed tick.
/// </summary>
public sealed class KiteWebSocketMarketDataProvider : ILiveMarketDataProvider, IDisposable
{
    private readonly ILogger<KiteWebSocketMarketDataProvider> _logger;
    private readonly BrokerSettings _settings;
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _cts;
    private Task? _receiveTask;
    private volatile bool _isConnected;
    private volatile bool _disposed;

    public string ProviderName => "KiteWebSocket";
    public bool IsConnected => _isConnected;

    public event EventHandler<TickEventArgs>? TickReceived;
    public event EventHandler<MarketDepthEventArgs>? DepthReceived;
    public event EventHandler<MarketDataDisconnectedEventArgs>? Disconnected;

    public KiteWebSocketMarketDataProvider(
        ILogger<KiteWebSocketMarketDataProvider> logger,
        IOptions<BrokerSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        _webSocket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected) return;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            throw new InvalidOperationException("Broker API key and access token are required for WebSocket connection.");
        }

        var url = $"wss://ws.kite.trade?api_key={_settings.ApiKey}&access_token={_settings.AccessToken}";
        _logger.LogInformation("Connecting to Kite WebSocket: {Url}", url);

        try
        {
            await _webSocket.ConnectAsync(new Uri(url), cancellationToken);
            _isConnected = true;
            _logger.LogInformation("Connected to Kite WebSocket");

            _receiveTask = ReceiveLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Kite WebSocket");
            throw;
        }
    }

    public async Task SubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default)
    {
        if (!_isConnected) throw new InvalidOperationException("WebSocket is not connected.");

        var tokens = instrumentTokens.ToList();
        if (tokens.Count == 0) return;

        // Kite subscribe message: [1, count, ...tokens]
        var message = new byte[4 + tokens.Count * 4];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0), 1); // message type: subscribe
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(8 + i * 4), tokens[i]);
        }

        await _webSocket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, cancellationToken);
        _logger.LogInformation("Subscribed to {Count} instruments", tokens.Count);
    }

    public async Task UnsubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default)
    {
        if (!_isConnected) return;

        var tokens = instrumentTokens.ToList();
        if (tokens.Count == 0) return;

        // Kite unsubscribe message: [2, count, ...tokens]
        var message = new byte[4 + tokens.Count * 4];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0), 2); // message type: unsubscribe
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(8 + i * 4), tokens[i]);
        }

        await _webSocket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, cancellationToken);
        _logger.LogInformation("Unsubscribed from {Count} instruments", tokens.Count);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected) return;

        _logger.LogInformation("Disconnecting from Kite WebSocket");
        _cts.Cancel();

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during WebSocket close");
        }

        if (_receiveTask != null)
        {
            await _receiveTask;
        }

        _isConnected = false;
        Disconnected?.Invoke(this, new MarketDataDisconnectedEventArgs { Reason = "Client disconnect" });
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket closed by server: {Status}", result.CloseStatus);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var message = new byte[result.Count];
                    Array.Copy(buffer, message, result.Count);
                    ProcessBinaryMessage(message);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogDebug("WebSocket text message: {Text}", text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket receive loop");
            Disconnected?.Invoke(this, new MarketDataDisconnectedEventArgs
            {
                Reason = "Receive loop error",
                Exception = ex
            });
        }
        finally
        {
            _isConnected = false;
        }
    }

    private void ProcessBinaryMessage(byte[] message)
    {
        try
        {
            // Kite binary protocol: [packet_count:2][packet_length:2][packet_data:packet_length]...
            if (message.Length < 2) return;

            var packetCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(0));
            var offset = 2;

            for (var i = 0; i < packetCount && offset + 2 <= message.Length; i++)
            {
                var packetLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
                offset += 2;

                if (offset + packetLength > message.Length)
                {
                    _logger.LogWarning("Packet {Index} truncated (expected {Expected}, got {Actual})",
                        i, packetLength, message.Length - offset);
                    break;
                }

                var packetData = message.AsSpan(offset, packetLength);
                ParseTickPacket(packetData);
                offset += packetLength;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing binary message");
        }
    }

    private void ParseTickPacket(ReadOnlySpan<byte> packet)
    {
        // Minimum packet size: instrument_token (4) + last_price (4) = 8 bytes
        if (packet.Length < 8) return;

        var instrumentToken = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(0, 4));
        var lastPriceRaw = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(4, 4));
        var lastPrice = lastPriceRaw / 100.0m;

        // Extract volume if available (offset 8, 4 bytes)
        long volume = 0;
        if (packet.Length >= 12)
        {
            var volumeRaw = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(8, 4));
            volume = volumeRaw;
        }

        // For Phase 3 scaffold, use last_price for bid/ask (no depth parsing yet)
        var tick = new Tick(
            InstrumentToken: instrumentToken,
            TimestampUtc: DateTimeOffset.UtcNow,
            LastPrice: lastPrice,
            BidPrice: lastPrice,
            AskPrice: lastPrice,
            Volume: volume);

        TickReceived?.Invoke(this, new TickEventArgs { Tick = tick });
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
