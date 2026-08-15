namespace AlgoTrader.Application.Safety;

/// <summary>Kill-switch position (§15).</summary>
public enum KillSwitchState
{
    Disengaged,
    Engaged
}

/// <summary>Event raised when the kill switch changes state.</summary>
public sealed class KillSwitchEventArgs : EventArgs
{
    public required KillSwitchState State { get; init; }
    public required string Reason { get; init; }
    public required string InitiatedBy { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Emergency stop (§15, §31). While engaged: no new orders may be accepted and pending
/// orders must be cancelled. Resuming requires an explicit reset by an operator — the
/// platform never auto-disengages.
/// </summary>
public interface IKillSwitch
{
    KillSwitchState State { get; }

    bool IsEngaged { get; }

    /// <summary>Reason recorded when the kill switch was last engaged.</summary>
    string? Reason { get; }

    /// <summary>Engages the kill switch. Idempotent — a second engage does not re-raise events.</summary>
    void Engage(string reason, string initiatedBy = "system");

    /// <summary>Explicit operator reset. Requires manual intervention by design (§15).</summary>
    void Reset(string initiatedBy = "operator");

    /// <summary>Raised whenever the kill switch state changes.</summary>
    event EventHandler<KillSwitchEventArgs>? StateChanged;
}
