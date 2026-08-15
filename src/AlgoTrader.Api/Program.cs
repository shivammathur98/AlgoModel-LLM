using AlgoTrader.Api.Endpoints;
using AlgoTrader.Application;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Broker;
using AlgoTrader.Infrastructure;
using AlgoTrader.MarketData;
using AlgoTrader.Persistence;
using Microsoft.Extensions.Options;
using Serilog;

// Bootstrap logger for startup errors before the full host is built
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("AlgoTrader API starting up");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog from appsettings
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    // Composition root: wire all layers
    builder.Services.AddAlgoTraderApplication(builder.Configuration);
    builder.Services.AddAlgoTraderInfrastructure();
    builder.Services.AddAlgoTraderBroker();
    builder.Services.AddAlgoTraderMarketData();
    builder.Services.AddAlgoTraderPersistence(builder.Configuration);

    // Health checks (§35)
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // Startup safety gate (§6, §36): ValidateOnStart already blocks a misconfigured Live mode.
    // Here we surface the mode and fail immediately if someone expects live trading but safety rejected it.
    var tradingSettings = app.Services.GetRequiredService<IOptions<TradingSettings>>().Value;
    var safety = app.Services.GetRequiredService<LiveTradingSafetyValidator>();

    if (tradingSettings.Mode == AlgoTrader.Domain.Enums.TradingMode.Live)
    {
        var validation = safety.ValidateForLiveTrading(tradingSettings);
        if (!validation.IsValid)
        {
            Log.Fatal("Live trading requested but safety validation failed: {Failures}", validation.Failures);
            throw new InvalidOperationException(
                "Live trading safety validation failed: " + string.Join("; ", validation.Failures));
        }

        Log.Warning(
            "LIVE TRADING MODE IS ACTIVE. Real orders will be sent to the broker once trading is started. " +
            "Current configuration: {Mode}, EnableLiveTrading: {EnableLiveTrading}",
            tradingSettings.Mode, tradingSettings.EnableLiveTrading);
    }
    else
    {
        Log.Information(
            "AlgoTrader running in {TradingMode} mode. Real orders are disabled.",
            tradingSettings.Mode);
    }

    // Map endpoints
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready");
    app.MapStatusEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "AlgoTrader host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Expose Program for WebApplicationFactory in integration tests.
/// This partial class declaration is required for the test host to resolve the entry point.
/// </summary>
public partial class Program { }
