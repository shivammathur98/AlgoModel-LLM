namespace AlgoTrader.Broker.Zerodha;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Zerodha Kite Connect broker implementation (Â§4, Â§5, Â§7).
/// Implements both the order-management contract (<see cref="ITradingBroker"/>) and the
/// historical-data contract (<see cref="IHistoricalDataProvider"/>). Once authenticated it also owns a
/// <see cref="KiteOrderStream"/> that turns Kite order postbacks into <see cref="OrderUpdated"/> events; the
/// stream is disposed with the broker (<see cref="IDisposable"/>) when its DI scope ends.
/// </summary>
public sealed class ZerodhaKiteBroker : ITradingBroker, IHistoricalDataProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly IInstrumentRepository _instruments;
    private readonly ILogger<ZerodhaKiteBroker> _logger;
    private readonly BrokerSettings _settings;
    private volatile bool _isAuthenticated;
    private KiteOrderStream? _orderStream;
    private bool _disposed;

    public string ProviderName => "Zerodha";
    public bool IsAuthenticated => _isAuthenticated;
    public bool IsConnected => _isAuthenticated && (_orderStream?.IsRunning ?? false);

    /// <summary>Raised when the broker pushes an asynchronous order status update (fill, cancel, reject).</summary>
    /// <remarks>
    /// Backed by <see cref="KiteOrderStream"/>: once <see cref="AuthenticateAsync"/> succeeds, a dedicated Kite
    /// ticker WebSocket listens for order postbacks and forwards each as a <see cref="BrokerOrderUpdate"/>. The
    /// stream lives for the authenticated session and is torn down by <see cref="Dispose"/>.
    /// </remarks>
    public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
    public event EventHandler<EventArgs>? StreamDisconnected;

    public ZerodhaKiteBroker(
        HttpClient http,
        IInstrumentRepository instruments,
        ILogger<ZerodhaKiteBroker> logger,
        IOptions<BrokerSettings> settings)
    {
        _http = http;
        _instruments = instruments;
        _logger = logger;
        _settings = settings.Value;

        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri("https://api.kite.trade");
        }

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("X-Kite-Version", "3");

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey) && !string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("token", $"{_settings.ApiKey}:{_settings.AccessToken}");
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // ITradingBroker
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Validates the configured access token against Kite's /user/profile endpoint.
    /// Kite Connect access tokens are regenerated daily via the login flow; this method
    /// is intentionally a lightweight validation, not a fresh authentication.
    /// </summary>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            _isAuthenticated = false;
            throw new InvalidOperationException("Broker access token is not configured.");
        }

        var response = await _http.GetAsync("/user/profile", cancellationToken);
        response.EnsureSuccessStatusCode();
        _isAuthenticated = true;
        _logger.LogInformation("Authenticated with Zerodha Kite Connect");

        // Start the async order-postback channel so fills/cancels/rejects surface as OrderUpdated (Â§7).
        await StartOrderStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the dedicated Kite order-postback stream so <see cref="OrderUpdated"/> fires for asynchronous
    /// fills, cancels and rejects. Non-fatal: if the stream cannot start it is logged and REST order management
    /// keeps working (fills are then only learned at end-of-day reconciliation), so authentication still succeeds.
    /// </summary>
    private async Task StartOrderStreamAsync(CancellationToken cancellationToken)
    {
        if (_orderStream is not null)
        {
            if (_orderStream.IsRunning) return; // already streaming for this session
            
            // Reconnecting a disconnected stream
            _orderStream.OrderUpdated -= OnStreamOrderUpdated;
            _orderStream.Disconnected -= OnStreamDisconnected;
            _orderStream.Dispose();
            _orderStream = null;
        }

        var stream = new KiteOrderStream(_settings, _logger);
        try
        {
            stream.OrderUpdated += OnStreamOrderUpdated;
            stream.Disconnected += OnStreamDisconnected;
            await stream.StartAsync(cancellationToken).ConfigureAwait(false);
            _orderStream = stream;
        }
        catch (Exception ex)
        {
            // Dispose the half-started stream so its socket/CTS don't leak; REST order management keeps working.
            stream.OrderUpdated -= OnStreamOrderUpdated;
            stream.Dispose();
            _logger.LogWarning(ex,
                "Kite order stream failed to start; asynchronous order updates are unavailable this session.");
        }
    }

    private void OnStreamOrderUpdated(object? sender, BrokerOrderUpdate update) => OrderUpdated?.Invoke(this, update);
    private void OnStreamDisconnected(object? sender, EventArgs e) => StreamDisconnected?.Invoke(this, e);

    public async Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var json = await _http.GetFromJsonAsync<JsonElement>("/user/profile", cancellationToken);
        var data = json.GetProperty("data");

        var exchanges = new List<string>();
        if (data.TryGetProperty("exchanges", out var exch) && exch.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in exch.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) exchanges.Add(e.GetString()!);
        }

        return new BrokerProfile(
            ClientId: data.GetProperty("user_id").GetString() ?? string.Empty,
            Name: data.GetProperty("user_name").GetString() ?? string.Empty,
            EnabledExchanges: exchanges);
    }

    public async Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var json = await _http.GetFromJsonAsync<JsonElement>("/user/margins/equity", cancellationToken);
        var data = json.GetProperty("data");

        decimal availableCash = 0m, availableMargin = 0m, usedMargin = 0m;

        if (data.TryGetProperty("net", out var net)) usedMargin = net.GetDecimal();
        if (data.TryGetProperty("available", out var available))
        {
            if (available.TryGetProperty("cash", out var cash)) availableCash = cash.GetDecimal();
            if (available.TryGetProperty("live_balance", out var live)) availableMargin = live.GetDecimal();
            else availableMargin = availableCash;
        }

        return new BrokerFunds(availableCash, usedMargin, availableMargin);
    }

    public async Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var json = await _http.GetFromJsonAsync<JsonElement>("/portfolio/holdings", cancellationToken);
        var data = json.GetProperty("data");

        var holdings = new List<BrokerHolding>();
        foreach (var h in data.EnumerateArray())
        {
            holdings.Add(new BrokerHolding(
                Symbol: h.GetProperty("tradingsymbol").GetString() ?? string.Empty,
                InstrumentToken: h.GetProperty("instrument_token").GetInt32(),
                Quantity: h.GetProperty("quantity").GetInt32(),
                AveragePrice: h.GetProperty("average_price").GetDecimal(),
                LastPrice: h.GetProperty("last_price").GetDecimal()));
        }
        return holdings;
    }

    public async Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var json = await _http.GetFromJsonAsync<JsonElement>("/portfolio/positions", cancellationToken);
        var net = json.GetProperty("data").GetProperty("net");

        var positions = new List<BrokerPositionSummary>();
        foreach (var p in net.EnumerateArray())
        {
            var qty = p.GetProperty("quantity").GetInt32();
            if (qty == 0) continue;

            var instrument = await _instruments.GetByTokenAsync(
                p.GetProperty("instrument_token").GetInt32(), cancellationToken);
            if (instrument == null) continue;

            var side = qty > 0 ? OrderSide.Buy : OrderSide.Sell;
            var product = ParseProduct(p.GetProperty("product").GetString());
            var unrealised = p.TryGetProperty("unrealised", out var ur) ? ur.GetDecimal() : 0m;

            positions.Add(new BrokerPositionSummary(
                instrument.Symbol, instrument.InstrumentToken, product, side,
                Math.Abs(qty),
                p.GetProperty("average_price").GetDecimal(),
                unrealised));
        }
        return positions;
    }

    public async Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var json = await _http.GetFromJsonAsync<JsonElement>("/orders", cancellationToken);
        var data = json.GetProperty("data");

        var orders = new List<BrokerOrderInfo>();
        foreach (var o in data.EnumerateArray())
        {
            var info = MapOrderInfo(o);
            if (info != null) orders.Add(info);
        }
        return orders;
    }

    public async Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var json = await _http.GetFromJsonAsync<JsonElement>($"/orders/{brokerOrderId}", cancellationToken);
        var data = json.GetProperty("data");

        // Kite returns the current state of the order as the last entry in the array
        BrokerOrderInfo? latest = null;
        foreach (var o in data.EnumerateArray())
        {
            var info = MapOrderInfo(o);
            if (info != null) latest = info;
        }

        return latest ?? throw new InvalidOperationException($"Order {brokerOrderId} not found.");
    }

    public async Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        EnsureArgumentNotNull(request, nameof(request));

        var form = new Dictionary<string, string>
        {
            ["tradingsymbol"] = request.Symbol,
            ["exchange"] = request.Exchange,
            ["transaction_type"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["quantity"] = request.Quantity.ToString(),
            ["order_type"] = MapOrderType(request.Type),
            ["product"] = MapProduct(request.Product),
            ["validity"] = MapValidity(request.Validity),
        };

        if (request.Price.HasValue) form["price"] = request.Price.Value.ToString("0.####");
        if (request.TriggerPrice.HasValue) form["trigger_price"] = request.TriggerPrice.Value.ToString("0.####");
        if (!string.IsNullOrEmpty(request.Tag)) form["tag"] = request.Tag;

        _logger.LogInformation(
            "Placing {Side} {Type} order for {Symbol} qty={Qty} (correlation={Corr})",
            request.Side, request.Type, request.Symbol, request.Quantity, request.CorrelationId);

        try
        {
            using var content = new FormUrlEncodedContent(form!);
            var response = await _http.PostAsync("/orders/regular", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var msg = ExtractErrorMessage(body);
                // Distinguish a DEFINITIVE rejection from an AMBIGUOUS one (Â§20, Rules #8/#9). A 4xx is the
                // exchange/RMS refusing the order â€” it was not placed, so it is safe to treat as terminal. A 5xx
                // (or any other non-success) means the upstream may have received/processed the order before
                // failing to respond cleanly â€” the order may be live at the broker, so it is UNCERTAIN.
                var isDefinitiveRejection = (int)response.StatusCode is >= 400 and < 500;
                if (isDefinitiveRejection)
                {
                    _logger.LogWarning("Order placement rejected by broker: {Message}", msg);
                    return new PlaceOrderResult(false, null, msg);
                }

                _logger.LogError("Order placement failed with {Status}; UNCERTAIN â€” order may be live at broker: {Message}",
                    (int)response.StatusCode, msg);
                return new PlaceOrderResult(false, null, msg, IsUncertain: true);
            }

            var json = JsonDocument.Parse(body);
            var orderId = json.RootElement.GetProperty("data").GetProperty("order_id").GetString();
            if (string.IsNullOrEmpty(orderId))
            {
                // A success status means Kite accepted the order, but we could not read its id â€” the order is
                // live at the broker under an id we did not capture. UNCERTAIN, never a clean rejection.
                _logger.LogError("Order accepted (HTTP success) but order_id was not readable; UNCERTAIN â€” order is live at broker.");
                return new PlaceOrderResult(false, null, "Order accepted but order id was not returned.", IsUncertain: true);
            }

            _logger.LogInformation("Order placed: {OrderId}", orderId);
            return new PlaceOrderResult(true, orderId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine caller cancellation (host shutdown / caller token) â€” propagate; not an order outcome.
            throw;
        }
        catch (Exception ex)
        {
            // HttpRequestException (connection reset / DNS / socket), a client-side HttpClient TIMEOUT
            // (TaskCanceledException : OperationCanceledException with our token NOT cancelled), or a malformed
            // success body: in every case we cannot tell whether the exchange received the order. UNCERTAIN
            // (Â§20, Rules #8/#9) â€” the caller must reconcile before assuming a non-fill or retrying.
            _logger.LogError(ex, "Order placement failed ambiguously; UNCERTAIN â€” order may be live at broker.");
            return new PlaceOrderResult(false, null, ex.Message, IsUncertain: true);
        }
    }

    public async Task<ModifyOrderResult> ModifyOrderAsync(
        string brokerOrderId,
        OrderModification modification,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        if (string.IsNullOrEmpty(brokerOrderId))
            return new ModifyOrderResult(false, "Order id is required.");

        var form = new Dictionary<string, string>();
        if (modification.Quantity.HasValue) form["quantity"] = modification.Quantity.Value.ToString();
        if (modification.Price.HasValue) form["price"] = modification.Price.Value.ToString("0.####");
        if (modification.TriggerPrice.HasValue) form["trigger_price"] = modification.TriggerPrice.Value.ToString("0.####");
        if (modification.Validity.HasValue) form["validity"] = MapValidity(modification.Validity.Value);

        try
        {
            using var content = new FormUrlEncodedContent(form!);
            var response = await _http.PutAsync($"/orders/regular/{brokerOrderId}", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var msg = ExtractErrorMessage(body);
                return new ModifyOrderResult(false, msg);
            }

            return new ModifyOrderResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to modify broker order {BrokerOrderId}", brokerOrderId);
            return new ModifyOrderResult(false, ex.Message);
        }
    }

    public async Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        if (string.IsNullOrEmpty(brokerOrderId))
            throw new ArgumentException("Order id is required.", nameof(brokerOrderId));

        var response = await _http.DeleteAsync($"/orders/regular/{brokerOrderId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // IHistoricalDataProvider
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var interval = MapTimeframe(timeframe);

        // Kite API expects IST timestamps for the historical endpoint.
        var fromIst = fromUtc.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd+HH:mm:ss");
        var toIst = toUtc.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd+HH:mm:ss");

        var url = $"/instruments/historical/{instrumentToken}/{interval}?from={fromIst}&to={toIst}";

        var json = await _http.GetFromJsonAsync<JsonElement>(url, cancellationToken);
        var data = json.GetProperty("data");

        if (!data.TryGetProperty("candles", out var candles))
            return Array.Empty<Candle>();

        var instrument = await _instruments.GetByTokenAsync(instrumentToken, cancellationToken);
        var symbol = instrument?.Symbol ?? $"TOKEN_{instrumentToken}";
        var exchange = instrument?.Exchange ?? "NSE";

        var result = new List<Candle>(candles.GetArrayLength());
        foreach (var c in candles.EnumerateArray())
        {
            var ts = DateTimeOffset.Parse(c[0].GetString()!).ToUniversalTime();
            result.Add(new Candle(
                InstrumentToken: instrumentToken,
                Symbol: symbol,
                Exchange: exchange,
                Timeframe: timeframe,
                TimestampUtc: ts,
                Open: c[1].GetDecimal(),
                High: c[2].GetDecimal(),
                Low: c[3].GetDecimal(),
                Close: c[4].GetDecimal(),
                Volume: (long)c[5].GetDecimal()));
        }

        return result;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Helpers
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void EnsureAuthenticated()
    {
        if (!_isAuthenticated)
            throw new InvalidOperationException("Broker is not authenticated. Call AuthenticateAsync first.");
    }

    private static void EnsureArgumentNotNull(object? value, string name)
    {
        if (value == null) throw new ArgumentNullException(name);
    }

    private BrokerOrderInfo? MapOrderInfo(JsonElement o)
    {
        var symbol = o.TryGetProperty("tradingsymbol", out var ts) ? ts.GetString() ?? "" : "";
        var orderId = o.TryGetProperty("order_id", out var oid) ? oid.GetString() ?? "" : "";
        var instrumentToken = o.TryGetProperty("instrument_token", out var it) ? it.GetInt32() : 0;
        var side = o.TryGetProperty("transaction_type", out var st) && st.GetString() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
        var type = ParseOrderType(o.TryGetProperty("order_type", out var ot) ? ot.GetString() : null);
        var qty = o.TryGetProperty("quantity", out var q) ? q.GetInt32() : 0;
        var price = o.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? (decimal?)p.GetDecimal() : null;
        var filled = o.TryGetProperty("filled_quantity", out var fq) ? fq.GetInt32() : 0;
        var state = ParseOrderState(o.TryGetProperty("status", out var s) ? s.GetString() : null, filled);
        var avgFill = o.TryGetProperty("average_price", out var ap) && ap.ValueKind == JsonValueKind.Number ? (decimal?)ap.GetDecimal() : null;
        var statusMsg = o.TryGetProperty("status_message", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null;

        return new BrokerOrderInfo(orderId, symbol, instrumentToken, side, type, qty, price, state, filled, avgFill, statusMsg);
    }

    internal static OrderState ParseOrderState(string? status, int filledQuantity = 0) => status switch
    {
        null => OrderState.Pending,
        "COMPLETE" => OrderState.Filled,
        "CANCELLED" => OrderState.Cancelled,
        "REJECTED" => OrderState.Rejected,
        "OPEN" => filledQuantity > 0 ? OrderState.PartiallyFilled : OrderState.Open,
        "PUT ORDER REQ RECEIVED" or "VALIDATION PENDING" or "OPEN PENDING" or "AMO REQ RECEIVED" or "TRIGGER PENDING" => OrderState.Pending,
        _ => OrderState.Pending,
    };

    internal static OrderType ParseOrderType(string? type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        "SL" => OrderType.StopLossLimit,
        "SL-M" => OrderType.StopLoss,
        _ => OrderType.Market,
    };

    internal static ProductType ParseProduct(string? product) => product switch
    {
        "MIS" => ProductType.Intraday,
        "CNC" or "NRML" => ProductType.Delivery,
        _ => ProductType.Intraday,
    };

    internal static string MapOrderType(OrderType type) => type switch
    {
        OrderType.Market => "MARKET",
        OrderType.Limit => "LIMIT",
        OrderType.StopLoss => "SL-M",
        OrderType.StopLossLimit => "SL",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static string MapProduct(ProductType product) => product switch
    {
        ProductType.Intraday => "MIS",
        ProductType.Delivery => "CNC",
        _ => throw new ArgumentOutOfRangeException(nameof(product)),
    };

    internal static string MapValidity(OrderValidity validity) => validity switch
    {
        OrderValidity.Day => "DAY",
        OrderValidity.Ioc => "IOC",
        _ => "DAY",
    };

    internal static string MapTimeframe(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "minute",
        Timeframe.Minute5 => "5minute",
        Timeframe.Minute15 => "15minute",
        Timeframe.Minute30 => "30minute",
        Timeframe.Minute60 => "60minute",
        Timeframe.Daily => "day",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe)),
    };

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString() ?? body;
            if (json.RootElement.TryGetProperty("error", out var err))
                return err.GetString() ?? body;
        }
        catch
        {
            // Fall through to raw body.
        }
        return body;
    }

    /// <summary>
    /// Raises <see cref="OrderUpdated"/>. Exposed for unit tests that want to drive the event pipeline
    /// directly (the live path raises it from the <see cref="KiteOrderStream"/> postback handler).
    /// </summary>
    internal void RaiseOrderUpdated(BrokerOrderUpdate update) => OrderUpdated?.Invoke(this, update);

    /// <summary>Tears down the order-postback stream. Invoked when the broker's DI scope is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_orderStream is not null)
        {
            _orderStream.OrderUpdated -= OnStreamOrderUpdated;
            _orderStream.Dispose();
            _orderStream = null;
        }
    }
}


