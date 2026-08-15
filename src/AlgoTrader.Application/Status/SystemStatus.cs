namespace AlgoTrader.Application.Status;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;
using Microsoft.Extensions.Options;

/// <summary>Snapshot of the platform's operational state, exposed via /api/status (§35).</summary>
public sealed record SystemStatus(
    TradingMode Mode,
    bool LiveTradingEnabled,
    KillSwitchState KillSwitchState,
    string? KillSwitchReason,
    DateTimeOffset StartedAtUtc,
    double UptimeSeconds,
    string ApplicationVersion);

/// <summary>Provides the current system status.</summary>
public interface ISystemStatusService
{
    SystemStatus GetStatus();
}

/// <summary>
/// Production system status service. Captures startup time on construction and computes uptime on demand.
/// </summary>
public sealed class SystemStatusService : ISystemStatusService
{
    private readonly TradingSettings _trading;
    private readonly IKillSwitch _killSwitch;
    private readonly ISystemClock _clock;
    private readonly DateTimeOffset _startedAtUtc;

    public SystemStatusService(IOptions<TradingSettings> trading, IKillSwitch killSwitch, ISystemClock clock)
    {
        _trading = trading.Value;
        _killSwitch = killSwitch;
        _clock = clock;
        _startedAtUtc = clock.UtcNow;
    }

    public SystemStatus GetStatus()
    {
        var now = _clock.UtcNow;
        var uptime = (now - _startedAtUtc).TotalSeconds;
        var version = typeof(SystemStatusService).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        return new SystemStatus(
            Mode: _trading.Mode,
            LiveTradingEnabled: _trading.EnableLiveTrading,
            KillSwitchState: _killSwitch.State,
            KillSwitchReason: _killSwitch.Reason,
            StartedAtUtc: _startedAtUtc,
            UptimeSeconds: uptime,
            ApplicationVersion: version);
    }
}
