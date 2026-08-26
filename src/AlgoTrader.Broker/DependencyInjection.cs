using System;
using System.Net.Http;
using AlgoTrader.Broker.Zerodha;
using AlgoTrader.Domain.Broker;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;

namespace AlgoTrader.Broker;

/// <summary>DI registration for the Broker layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds the configured broker adapter (<see cref="ZerodhaKiteBroker"/>).</summary>
    public static IServiceCollection AddAlgoTraderBroker(this IServiceCollection services)
    {
        // Typed HttpClient: a ZerodhaKiteBroker is created per-scope with a configured HttpClient.
        services.AddHttpClient<ZerodhaKiteBroker>()
            .AddPolicyHandler(request => request.Method == HttpMethod.Get ? GetRetryPolicy() : Policy.NoOpAsync<HttpResponseMessage>());

        // Broker order/account contract. Historical market data is registered by the MarketData
        // module so it remains independently replaceable and testable.
        services.AddScoped<ITradingBroker>(sp => sp.GetRequiredService<ZerodhaKiteBroker>());

        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError() // Handles 5xx and 408
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // Handle 429
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
    }
}
