namespace AlgoTrader.Application.Safety;

using AlgoTrader.Domain.Common;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory kill switch. Persistence of kill-switch events lands with the
/// RiskEvent/SystemEvent tables in the persistence phase; the engagement itself must work
/// even when the database is unavailable (§33).
/// </summary>
public sealed class KillSwitchService : IKillSwitch
{
    private readonly ISystemClock _clock;
    private readonly ILogger<KillSwitchService> _logger;
    private readonly object _gate = new();

    private KillSwitchState _state = KillSwitchState.Disengaged;
    private string? _reason;

    public KillSwitchService(ISystemClock clock, ILogger<KillSwitchService> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    public KillSwitchState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool IsEngaged => State == KillSwitchState.Engaged;

    public string? Reason
    {
        get { lock (_gate) { return _reason; } }
    }

    public event EventHandler<KillSwitchEventArgs>? StateChanged;

    public void Engage(string reason, string initiatedBy = "system")
    {
        var timestamp = _clock.UtcNow;

        lock (_gate)
        {
            if (_state == KillSwitchState.Engaged)
            {
                return; // idempotent
            }

            _state = KillSwitchState.Engaged;
            _reason = reason;
        }

        _logger.LogCritical(
            "KILL SWITCH ENGAGED by {InitiatedBy} at {TimestampUtc}: {Reason}",
            initiatedBy, timestamp, reason);

        StateChanged?.Invoke(this, new KillSwitchEventArgs
        {
            State = KillSwitchState.Engaged,
            Reason = reason,
            InitiatedBy = initiatedBy,
            TimestampUtc = timestamp
        });
    }

    public void Reset(string initiatedBy = "operator")
    {
        var timestamp = _clock.UtcNow;

        lock (_gate)
        {
            if (_state == KillSwitchState.Disengaged)
            {
                return; // idempotent
            }

            _state = KillSwitchState.Disengaged;
            _reason = null;
        }

        _logger.LogWarning(
            "Kill switch reset by {InitiatedBy} at {TimestampUtc}. Trading may resume only after reconciliation checks pass.",
            initiatedBy, timestamp);

        StateChanged?.Invoke(this, new KillSwitchEventArgs
        {
            State = KillSwitchState.Disengaged,
            Reason = "reset",
            InitiatedBy = initiatedBy,
            TimestampUtc = timestamp
        });
    }
}
