namespace AlgoTrader.Application.Safety;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Enums;

/// <summary>Outcome of a safety validation.</summary>
public sealed record SafetyValidationResult(bool IsValid, IReadOnlyList<string> Failures)
{
    public static SafetyValidationResult Success { get; } = new(true, Array.Empty<string>());

    public static SafetyValidationResult Failed(params string[] failures) => new(false, failures);
}

/// <summary>
/// Guard that makes accidental live trading practically impossible (§6, §36). Live trading
/// requires all three gates: Mode == Live, EnableLiveTrading == true, and the exact
/// acknowledgement phrase. Anything less must never result in a real order.
/// </summary>
public sealed class LiveTradingSafetyValidator
{
    /// <summary>
    /// Full validation required before any real order may be sent
    /// (used at startup and by the live-start endpoint).
    /// </summary>
    public SafetyValidationResult ValidateForLiveTrading(TradingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var failures = new List<string>();

        if (settings.Mode != TradingMode.Live)
        {
            failures.Add($"Trading:Mode must be '{TradingMode.Live}' but is '{settings.Mode}'.");
        }

        if (!settings.EnableLiveTrading)
        {
            failures.Add("Trading:EnableLiveTrading must be true.");
        }

        if (!string.Equals(settings.LiveTradingAcknowledgement, TradingSettings.RequiredLiveAcknowledgement, StringComparison.Ordinal))
        {
            failures.Add($"Trading:LiveTradingAcknowledgement must be exactly '{TradingSettings.RequiredLiveAcknowledgement}'.");
        }

        return failures.Count == 0
            ? SafetyValidationResult.Success
            : SafetyValidationResult.Failed(failures.ToArray());
    }

    /// <summary>
    /// Startup gate used by options validation: when the configured mode is Live but the
    /// other gates are not satisfied, the host must refuse to start. Non-live modes pass.
    /// </summary>
    public static bool StartupConfigurationIsValid(TradingSettings settings)
        => settings.Mode != TradingMode.Live
           || (settings.EnableLiveTrading
               && string.Equals(settings.LiveTradingAcknowledgement, TradingSettings.RequiredLiveAcknowledgement, StringComparison.Ordinal));
}
