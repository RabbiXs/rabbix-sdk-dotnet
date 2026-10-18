using System.Text.Json.Serialization;

namespace RabbiX.Sdk;

/// <summary>Who decides whether a proposed action may be committed.</summary>
public enum Route
{
    /// <summary>A value this SDK version does not know.</summary>
    Unknown,
    /// <summary>The agent may commit without a human.</summary>
    AiAutonomous,
    /// <summary>A human approves or rejects; the agent commits after approval.</summary>
    HumanInTheLoop,
    /// <summary>A human takes over; the agent must not commit unless the action is released.</summary>
    HumanOnly,
}

/// <summary>Lifecycle state of a proposed action.</summary>
public enum ActionStatus
{
    /// <summary>A value this SDK version does not know.</summary>
    Unknown,
    /// <summary>Received, not yet decided.</summary>
    Proposed,
    /// <summary>The agent may commit.</summary>
    Approved,
    /// <summary>Waiting for a human decision.</summary>
    Pending,
    /// <summary>A human rejected the action.</summary>
    Rejected,
    /// <summary>Nobody decided in time.</summary>
    Expired,
    /// <summary>A human took over; the agent must not commit.</summary>
    HandedOff,
    /// <summary>A handed-off action was released back to the agent, which may now commit.</summary>
    Released,
    /// <summary>A human declined the handoff.</summary>
    Declined,
    /// <summary>A human performed the action themselves.</summary>
    DoneByHuman,
    /// <summary>The agent reported a successful commit.</summary>
    Committed,
    /// <summary>The agent reported a failed commit.</summary>
    Failed,
}

/// <summary>Final status of a session.</summary>
public enum SessionStatus
{
    /// <summary>A value this SDK version does not know.</summary>
    Unknown,
    /// <summary>The session is still open.</summary>
    Active,
    /// <summary>The agent reached its goal.</summary>
    Completed,
    /// <summary>The agent failed.</summary>
    Failed,
    /// <summary>The work was cancelled.</summary>
    Cancelled,
}

/// <summary>What the SDK does when RabbiX is unreachable; set per organization and returned with every decision.</summary>
public enum FailMode
{
    /// <summary>Block: <c>ProposeAsync</c> throws <see cref="RabbiXUnavailableException"/> (default).</summary>
    Closed,
    /// <summary>Allow: <c>ProposeAsync</c> returns a degraded approval.</summary>
    Open,
}

/// <summary>One check RabbiX ran against the proposal (e.g. a claim compared with the evidence).</summary>
public sealed record Finding(string Check, string Result, string? Detail, string? Claim, string? Evidence);

/// <summary>RabbiX's answer to a proposed action.</summary>
public sealed class Decision
{
    [JsonIgnore]
    private readonly RabbiXClient? _client;

    internal Decision(RabbiXClient? client) => _client = client;

    /// <summary>Id of the action; <c>null</c> for a <see cref="Degraded"/> decision.</summary>
    public string? ActionId { get; init; }

    /// <summary>Route RabbiX chose.</summary>
    public Route? Route { get; init; }

    /// <summary>Current status.</summary>
    public ActionStatus Status { get; init; }

    /// <summary>Handoff id when the route is <see cref="Sdk.Route.HumanOnly"/>.</summary>
    public string? HandoffId { get; init; }

    /// <summary>True when RabbiX was unreachable and the organization's fail mode is open.</summary>
    public bool Degraded { get; init; }

    /// <summary>Human-readable reason for the route.</summary>
    public string? Reason { get; init; }

    /// <summary><c>policy</c>, <c>classifier</c> or <c>fail_mode</c>.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>Checks RabbiX ran.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    /// <summary>Tags on the action.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>When a pending decision expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True when the agent may commit (<see cref="ActionStatus.Approved"/> or <see cref="ActionStatus.Released"/>).</summary>
    [JsonIgnore]
    public bool Approved => Status is ActionStatus.Approved or ActionStatus.Released;

    /// <summary>
    /// Reports the outcome of the commit and returns the new action status.
    /// No-op for a <see cref="Degraded"/> decision (no action id): it returns <see cref="Status"/> without a request.
    /// </summary>
    public Task<ActionStatus> ReportAsync(bool success, object? output = null, string? error = null, CancellationToken cancellationToken = default) =>
        ActionId is null || _client is null
            ? Task.FromResult(Status)
            : _client.ReportAsync(ActionId, success, output, error, cancellationToken);
}
