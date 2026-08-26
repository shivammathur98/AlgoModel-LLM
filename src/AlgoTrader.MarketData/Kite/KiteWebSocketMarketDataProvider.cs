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
    private ClientWebSocket _webSocket = null!;
    private readonly CancellationTokenSource _cts;
    private Task? _receiveTask;
    private volatile bool _isConnected;
    private volatile bool _disposed;

    public string ProviderName => "KiteWebSocket";
    public bool IsConnected => _isConnected;

    public event EventHandler<TickEventArgs>? TickReceived;
    public event EventHandler<MarketDepthEventArgs>? DepthReceived;
    public event EventHandler<MarketDataDisconnectedEventArgs>? Disconnected;

    private readonly HashSet<int> _subscribedTokens = new();

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

        // The URL carries the api_key and access_token as query parameters — never log it (§5).
        var url = $"wss://ws.kite.trade?api_key={_settings.ApiKey}&access_token={_settings.AccessToken}";
        _logger.LogInformation("Connecting to Kite WebSocket");

        try
        {
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri(url), cancellationToken);
            _isConnected = true;
            _logger.LogInformation("Connected to Kite WebSocket");

            _receiveTask = ReceiveLoopAsync(_cts.Token);

            if (_subscribedTokens.Count > 0)
            {
                await SubscribeInternalAsync(_subscribedTokens.ToList(), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Kite WebSocket");
            throw;
        }
    }

    public async Task SubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default)
    {
        var tokens = instrumentTokens.ToList();
        if (tokens.Count == 0) return;

        foreach (var t in tokens) _subscribedTokens.Add(t);

        if (!_isConnected) return;

        await SubscribeInternalAsync(tokens, cancellationToken);
    }

    private async Task SubscribeInternalAsync(List<int> tokens, CancellationToken cancellationToken)
    {
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
        var tokens = instrumentTokens.ToList();
        foreach (var t in tokens) _subscribedTokens.Remove(t);

        if (!_isConnected) return;
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
        var message = new List<byte>();

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
                    // A single tick batch can exceed the 8192-byte buffer and arrive as several frames; accumulate
                    // until EndOfMessage so the length-prefixed packet framing is never split mid-packet.
                    message.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                    if (!result.EndOfMessage)
                        continue;

                    ProcessBinaryMessage(message.ToArray());
                    message.Clear();
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
        }
        finally
        {
            _isConnected = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                Disconnected?.Invoke(this, new MarketDataDisconnectedEventArgs { Reason = "WebSocket closed" });
            }
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

    // Kite binary packet sizes (bytes) for a tradable NSE instrument (§7):
    // 8 = LTP mode, 44 = quote mode (adds OHLC + day volume), 184 = full mode (adds the 5x2 order book).
    private const int LtpPacketLength = 8;
    private const int QuotePacketLength = 44;
    private const int FullPacketLength = 184;

    // Market-depth block: 5 buy levels then 5 sell levels, each 12 bytes (qty:4, price:4, orders:2, pad:2).
    private const int DepthStartOffset = 64;
    private const int DepthLevelSize = 12;
    private const int DepthLevelsPerSide = 5;

    // Kite sends equity prices as integer paise; divide to get rupees.
    private const decimal PaisePerRupee = 100m;

    private void ParseTickPacket(ReadOnlySpan<byte> packet)
    {
        var parsed = TryParsePacket(packet, DateTimeOffset.UtcNow);
        if (parsed is null) return;

        TickReceived?.Invoke(this, new TickEventArgs { Tick = parsed.Value.Tick });

        if (parsed.Value.Depth is { } depth)
        {
            DepthReceived?.Invoke(this, new MarketDepthEventArgs { Depth = depth });
        }
    }

    /// <summary>
    /// Pure decoder for a single Kite binary tick packet (§7) — the unit under test. Always yields a
    /// <see cref="Tick"/>; a full-mode packet (<see cref="FullPacketLength"/> bytes) additionally yields a
    /// <see cref="MarketDepth"/> snapshot of the 5-level order book, and the tick's bid/ask are then taken from
    /// the top of that book. Shorter packets (LTP/quote mode) carry no book, so bid/ask fall back to the last
    /// price. Returns <c>null</c> for a runt packet too short to carry even a token and price.
    /// </summary>
    /// <param name="packet">One Kite packet, with the 2-byte length prefix already stripped by the framing layer.</param>
    /// <param name="receivedAtUtc">
    /// Receipt time, used as the tick/depth timestamp. Passed in (rather than read from the clock inside) so the
    /// decoder is deterministic under test. The packet's own exchange timestamp is intentionally not decoded here:
    /// Kite's epoch convention needs verifying against a live feed before it can be trusted for UTC storage.
    /// </param>
    public static KiteParsedPacket? TryParsePacket(ReadOnlySpan<byte> packet, DateTimeOffset receivedAtUtc)
    {
        if (packet.Length < LtpPacketLength) return null;

        var instrumentToken = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(0, 4));
        var lastPrice = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(4, 4)) / PaisePerRupee;

        // Day volume lives at offset 16 in quote/full packets (offset 8 is last-traded-quantity, not volume);
        // an 8-byte LTP packet carries no volume.
        long volume = 0;
        if (packet.Length >= QuotePacketLength)
        {
            volume = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(16, 4));
        }

        MarketDepth? depth = null;
        var bid = lastPrice;
        var ask = lastPrice;
        if (packet.Length >= FullPacketLength)
        {
            var buy = ParseDepthSide(packet, DepthStartOffset);
            var sell = ParseDepthSide(packet, DepthStartOffset + DepthLevelsPerSide * DepthLevelSize);
            depth = new MarketDepth(instrumentToken, receivedAtUtc, lastPrice, volume, buy, sell);

            // Best bid/ask = top of book; keep the last-price fallback if a side is empty (price 0).
            if (buy[0].Price > 0) bid = buy[0].Price;
            if (sell[0].Price > 0) ask = sell[0].Price;
        }

        var tick = new Tick(
            InstrumentToken: instrumentToken,
            TimestampUtc: receivedAtUtc,
            LastPrice: lastPrice,
            BidPrice: bid,
            AskPrice: ask,
            Volume: volume);

        return new KiteParsedPacket(tick, depth);
    }

    private static IReadOnlyList<MarketDepthLevel> ParseDepthSide(ReadOnlySpan<byte> packet, int startOffset)
    {
        var levels = new MarketDepthLevel[DepthLevelsPerSide];
        for (var i = 0; i < DepthLevelsPerSide; i++)
        {
            var o = startOffset + i * DepthLevelSize;
            var quantity = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(o, 4));
            var price = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(o + 4, 4)) / PaisePerRupee;
            var orders = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(o + 8, 2));
            levels[i] = new MarketDepthLevel(price, quantity, orders);
        }
        return levels;
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

/// <summary>
/// Decoded contents of one Kite binary packet: always a <see cref="Tick"/>, plus a <see cref="MarketDepth"/>
/// snapshot when the packet was full-mode (carried the order book).
/// </summary>
public readonly record struct KiteParsedPacket(Tick Tick, MarketDepth? Depth);
