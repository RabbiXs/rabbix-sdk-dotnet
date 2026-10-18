using System.Net;

namespace RabbiX.Sdk;

/// <summary>Base class for every error the SDK raises.</summary>
public class RabbiXException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RabbiXException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>HTTP status of the failed response, if there was one.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>400/422: the request was invalid.</summary>
public sealed class RabbiXValidationException(string message, HttpStatusCode statusCode, IReadOnlyDictionary<string, string[]>? errors)
    : RabbiXException(message, statusCode)
{
    /// <summary>Validation errors per field, when the API returned them.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}

/// <summary>401/403: the API key is missing, invalid or not allowed to do this.</summary>
public sealed class RabbiXAuthenticationException(string message, HttpStatusCode statusCode) : RabbiXException(message, statusCode);

/// <summary>404: the session or action does not exist.</summary>
public sealed class RabbiXNotFoundException(string message) : RabbiXException(message, HttpStatusCode.NotFound);

/// <summary>409: the request conflicts with the current state (e.g. reporting twice).</summary>
public sealed class RabbiXConflictException(string message) : RabbiXException(message, HttpStatusCode.Conflict);

/// <summary>RabbiX could not be reached after all retries (and the fail mode did not allow a degraded approval).</summary>
public sealed class RabbiXUnavailableException(string message, HttpStatusCode? statusCode, Exception? innerException, string? actionId = null)
    : RabbiXException(message, statusCode, innerException)
{
    /// <summary>Id of the action when RabbiX became unreachable while waiting for its decision; resume with <see cref="RabbiXClient.GetDecisionAsync"/>.</summary>
    public string? ActionId { get; } = actionId;
}

/// <summary>Base class for decisions that do not allow the agent to commit.</summary>
public abstract class RabbiXDecisionException(string message, Decision decision) : RabbiXException(message)
{
    /// <summary>The decision.</summary>
    public Decision Decision { get; } = decision;

    /// <summary>Id of the action; persist it to resume with <see cref="RabbiXClient.GetDecisionAsync"/>.</summary>
    public string? ActionId => Decision.ActionId;
}

/// <summary>The route is <c>human_only</c>: a human takes over. Do not commit; if the action is released later, resume with <see cref="RabbiXClient.GetDecisionAsync"/>.</summary>
public sealed class HandedOffException(Decision decision)
    : RabbiXDecisionException($"Action {decision.ActionId} was handed off to a human (handoff {decision.HandoffId}). Do not commit.", decision)
{
    /// <summary>Id of the handoff.</summary>
    public string? HandoffId => Decision.HandoffId;
}

/// <summary>A human rejected the action.</summary>
public sealed class RejectedException(Decision decision)
    : RabbiXDecisionException($"Action {decision.ActionId} was rejected: {decision.Reason}", decision);

/// <summary>Nobody decided before the action expired.</summary>
public sealed class ExpiredException(Decision decision)
    : RabbiXDecisionException($"Action {decision.ActionId} expired without a decision.", decision);

/// <summary>A human declined the handoff.</summary>
public sealed class DeclinedException(Decision decision)
    : RabbiXDecisionException($"Action {decision.ActionId} was declined.", decision);

/// <summary>
/// The <c>waitTimeout</c> passed to <c>ProposeAsync</c> elapsed while the action was still pending. Do not commit;
/// resume with <see cref="RabbiXClient.GetDecisionAsync"/>.
/// </summary>
public sealed class WaitTimeoutException(Decision decision)
    : RabbiXException($"Action {decision.ActionId} is still pending; the wait timed out.")
{
    /// <summary>The still-pending decision.</summary>
    public Decision Decision { get; } = decision;

    /// <summary>Id of the pending action.</summary>
    public string? ActionId => Decision.ActionId;
}
