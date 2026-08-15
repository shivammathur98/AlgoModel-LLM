namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Broker connection settings (§5). Secrets (ApiKey, ApiSecret, AccessToken) must come from
/// environment variables or a secret store — never commit real values. This type overrides
/// <see cref="ToString"/> to redact secrets; logging code must never serialize it manually.
/// </summary>
public sealed class BrokerSettings
{
    public const string SectionName = "Broker";

    /// <summary>Broker adapter to use. Currently "Zerodha"; later "Fyers", "Dhan".</summary>
    public string Provider { get; set; } = "Zerodha";

    /// <summary>Broker-side environment: "Paper" or "Live".</summary>
    public string Environment { get; set; } = "Paper";

    /// <summary>Kite Connect API key. Provide via environment variable Broker__ApiKey.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Kite Connect API secret. Provide via environment variable Broker__ApiSecret.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Daily access token obtained after login. Provide via Broker__AccessToken.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Redirect URL registered with the Kite app.</summary>
    public string RedirectUrl { get; set; } = string.Empty;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Secrets are redacted on purpose. Never log this object as data.</summary>
    public override string ToString()
        => $"Broker: {Provider}, Environment: {Environment}, ApiKey: [REDACTED], ApiSecret: [REDACTED], AccessToken: [REDACTED]";
}
