namespace AlgoTrader.Application.Configuration;

/// <summary>
/// Application-level logging settings (§28). Serilog itself is configured through the
/// "Serilog" configuration section; these settings cover app-level audit behaviour.
/// </summary>
public sealed class LoggingSettings
{
    public const string SectionName = "Logging";

    public string MinimumLevel { get; set; } = "Information";

    public bool ConsoleEnabled { get; set; } = true;

    public bool FileEnabled { get; set; } = true;

    /// <summary>Rolling file log path template.</summary>
    public string FilePath { get; set; } = "logs/algotrader-.log";

    /// <summary>Separate audit trail for trade-related events (§28).</summary>
    public string AuditLogPath { get; set; } = "logs/audit/algotrader-audit-.log";
}
