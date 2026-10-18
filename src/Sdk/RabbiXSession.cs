namespace RabbiX.Sdk;

/// <summary>
/// One unit of agent work towards a goal. Proposals made through the session are linked to it.
/// Disposing ends the session as <see cref="SessionStatus.Completed"/> unless <see cref="EndAsync"/> was called; on failure call <c>EndAsync(SessionStatus.Failed)</c> yourself.
/// </summary>
public sealed class RabbiXSession : IAsyncDisposable
{
    private readonly RabbiXClient _client;
    private int _ended;

    internal RabbiXSession(RabbiXClient client, string sessionId)
    {
        _client = client;
        SessionId = sessionId;
    }

    /// <summary>Id of the session.</summary>
    public string SessionId { get; }

    /// <summary>
    /// Proposes an effect before committing it. Returns when the agent may commit (or, without
    /// <paramref name="waitForApproval"/>, with a <see cref="ActionStatus.Pending"/> decision to resume later).
    /// </summary>
    /// <param name="action">Action type, e.g. <c>refunds.issue</c>.</param>
    /// <param name="proposal">The effect, serialized as JSON (a <see cref="System.Text.Json.JsonElement"/>, a dictionary or an object; object properties are sent snake_case).</param>
    /// <param name="reasoning">Why the proposal achieves the goal.</param>
    /// <param name="evidence">Facts the reasoning relies on (dictionary keys are sent as given).</param>
    /// <param name="tags">Tags for routing and audit.</param>
    /// <param name="callbackUrl">Webhook RabbiX calls when the decision changes.</param>
    /// <param name="waitForApproval">Long-poll while the decision is pending until a human decides.</param>
    /// <param name="idempotencyKey">Idempotency key; generated when omitted. Retries always reuse it.</param>
    /// <param name="waitTimeout">Stop waiting after this long and throw <see cref="WaitTimeoutException"/>; <c>null</c> waits until RabbiX decides or the action expires.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="HandedOffException">The route is <c>human_only</c>. Do not commit.</exception>
    /// <exception cref="RejectedException">A human rejected the action.</exception>
    /// <exception cref="ExpiredException">Nobody decided in time.</exception>
    /// <exception cref="DeclinedException">The action was declined.</exception>
    /// <exception cref="RabbiXUnavailableException">RabbiX is unreachable and the fail mode is closed, or it became unreachable while waiting (then <c>ActionId</c> is set; a pending action is never auto-approved).</exception>
    /// <exception cref="WaitTimeoutException"><paramref name="waitTimeout"/> elapsed while still pending. Do not commit.</exception>
    public Task<Decision> ProposeAsync(
        string action,
        object proposal,
        string? reasoning = null,
        object? evidence = null,
        IReadOnlyList<string>? tags = null,
        string? callbackUrl = null,
        bool waitForApproval = false,
        string? idempotencyKey = null,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default) =>
        _client.ProposeAsync(
            new ProposeRequest(SessionId, action, proposal, reasoning, evidence, tags, callbackUrl),
            waitForApproval, idempotencyKey, waitTimeout, cancellationToken);

    /// <summary>
    /// Ends the session. Call this with <see cref="SessionStatus.Failed"/> when the agent's work fails:
    /// <see cref="DisposeAsync"/> cannot see exceptions and always ends <see cref="SessionStatus.Completed"/>.
    /// A session is ended at most once; later calls (and disposal) do nothing and return <paramref name="status"/>.
    /// </summary>
    public Task<SessionStatus> EndAsync(SessionStatus status = SessionStatus.Completed, string? summary = null, CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _ended, 1) == 0
            ? _client.EndSessionAsync(SessionId, status, summary, cancellationToken)
            : Task.FromResult(status);

    /// <summary>Ends the session as completed unless <see cref="EndAsync"/> was called. Best effort: RabbiX errors are swallowed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _ended) != 0) return;
        try
        {
            await EndAsync().ConfigureAwait(false);
        }
        catch (RabbiXException)
        {
            // Disposal must not mask the agent's own exception. Call EndAsync to see end errors.
        }
    }
}
