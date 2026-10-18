using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RabbiX.Sdk;

/// <summary>Fail mode last seen in a decision response; shared by all clients from one DI container.</summary>
internal sealed class FailModeCache
{
    private volatile int _value = (int)FailMode.Closed;

    public FailMode Value
    {
        get => (FailMode)_value;
        set => _value = (int)value;
    }
}

/// <summary>Client for the RabbiX agent API. Thread-safe.</summary>
public sealed class RabbiXClient : IDisposable
{
    private const int MaxAttempts = 3;
    private const string LongPoll = "?wait=30s";
    private static readonly TimeSpan LongPollWait = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RabbiXOptions _options;
    private readonly string _baseUrl;
    private readonly FailModeCache _failMode;

    /// <summary>Creates a client with its own <see cref="HttpClient"/>. Prefer <c>services.AddRabbiX(...)</c> in hosted apps.</summary>
    public RabbiXClient(RabbiXOptions options)
        : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, options, new FailModeCache(), ownsHttp: true)
    {
    }

    /// <summary>Creates a client on top of an existing <see cref="HttpClient"/> (not disposed by this client).</summary>
    public RabbiXClient(HttpClient httpClient, RabbiXOptions options)
        : this(httpClient, options, new FailModeCache(), ownsHttp: false)
    {
    }

    internal RabbiXClient(HttpClient httpClient, RabbiXOptions options, FailModeCache failMode, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("RabbiXOptions.ApiKey is required.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BaseUrl, "options.BaseUrl");
        if (options.RetryBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "RabbiXOptions.RetryBackoff must not be negative.");
        if (options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "RabbiXOptions.RequestTimeout must be positive.");

        _http = httpClient;
        _ownsHttp = ownsHttp;
        _options = options;
        _baseUrl = options.BaseUrl.ToString().TrimEnd('/');
        _failMode = failMode;
    }

    /// <summary>The organization's fail mode as last returned by RabbiX (<see cref="FailMode.Closed"/> until a decision was seen).</summary>
    public FailMode FailMode => _failMode.Value;

    /// <summary>Starts a session: one unit of agent work towards <paramref name="goal"/>.</summary>
    public async Task<RabbiXSession> StartSessionAsync(string goal, string? agent = null, object? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var response = await SendAsync<StartSessionResponse>(
            HttpMethod.Post, "/v1/sessions", new StartSessionRequest(goal, agent, metadata), null, cancellationToken).ConfigureAwait(false);
        return new RabbiXSession(this, response.SessionId);
    }

    /// <summary>
    /// Reads the decision for an action, e.g. to resume after a pending or handed-off proposal.
    /// Never throws for the decision's status; check <see cref="Decision.Approved"/> before committing.
    /// </summary>
    /// <param name="actionId">Id of the action.</param>
    /// <param name="wait">Long-poll while the action is pending or handed off.</param>
    /// <param name="waitTimeout">Stop waiting after this long and return the current decision; <c>null</c> waits until it changes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Decision> GetDecisionAsync(string actionId, bool wait = false, TimeSpan? waitTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        if (!wait)
            return ToDecision(await GetActionAsync(actionId, longPoll: false, cancellationToken).ConfigureAwait(false));

        var view = await PollAsync(actionId, waitTimeout, s => s is ActionStatus.Pending or ActionStatus.HandedOff, cancellationToken).ConfigureAwait(false);
        return ToDecision(view);
    }

    /// <summary>Reports the outcome of committing an approved action. Returns the new action status.</summary>
    public async Task<ActionStatus> ReportAsync(string actionId, bool success, object? output = null, string? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        var response = await SendAsync<ReportResponse>(
            HttpMethod.Post, $"/v1/actions/{Uri.EscapeDataString(actionId)}/result", new ReportRequest(success, output, error), null, cancellationToken).ConfigureAwait(false);
        return response.Status;
    }

    internal async Task<SessionStatus> EndSessionAsync(string sessionId, SessionStatus status, string? summary, CancellationToken cancellationToken)
    {
        var response = await SendAsync<EndSessionResponse>(
            HttpMethod.Post, $"/v1/sessions/{Uri.EscapeDataString(sessionId)}/end", new EndSessionRequest(status, summary), null, cancellationToken).ConfigureAwait(false);
        return response.Status;
    }

    internal async Task<Decision> ProposeAsync(ProposeRequest request, bool waitForApproval, string? idempotencyKey, TimeSpan? waitTimeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Action);
        ArgumentNullException.ThrowIfNull(request.Proposal);

        ActionView view;
        try
        {
            view = await SendAsync<ActionView>(HttpMethod.Post, "/v1/actions", request, idempotencyKey ?? Guid.NewGuid().ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RabbiXUnavailableException) when (_failMode.Value == FailMode.Open)
        {
            return new Decision(this)
            {
                ActionId = null,
                Route = Sdk.Route.AiAutonomous,
                Status = ActionStatus.Approved,
                Degraded = true,
                DecidedBy = "fail_mode",
                Reason = "RabbiX unreachable; organization fail mode is open.",
                Tags = request.Tags ?? [],
            };
        }
        Observe(view);

        if (waitForApproval && view.Status == ActionStatus.Pending && view.ActionId is { } actionId)
        {
            try
            {
                view = await PollAsync(actionId, waitTimeout, s => s == ActionStatus.Pending, cancellationToken).ConfigureAwait(false);
            }
            catch (RabbiXUnavailableException ex)
            {
                // Fail-open applies to the initial proposal only: a pending human decision is never auto-approved.
                throw new RabbiXUnavailableException(
                    $"RabbiX became unreachable while waiting for the decision on action {actionId}. Do not commit; resume with GetDecisionAsync.",
                    ex.StatusCode, ex, actionId);
            }
            if (view.Status == ActionStatus.Pending)
                throw new WaitTimeoutException(ToDecision(view));
        }

        var decision = ToDecision(view);
        return decision.Status switch
        {
            ActionStatus.HandedOff => throw new HandedOffException(decision),
            ActionStatus.Rejected => throw new RejectedException(decision),
            ActionStatus.Expired => throw new ExpiredException(decision),
            ActionStatus.Declined => throw new DeclinedException(decision),
            _ => decision,
        };
    }

    /// <summary>Long-polls while <paramref name="keepWaiting"/> holds; returns the last view (still waiting only on timeout).</summary>
    private async Task<ActionView> PollAsync(string actionId, TimeSpan? timeout, Func<ActionStatus, bool> keepWaiting, CancellationToken cancellationToken)
    {
        var deadline = timeout is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
        ActionView view;
        do
        {
            view = await GetActionAsync(actionId, longPoll: true, cancellationToken).ConfigureAwait(false);
        }
        while (keepWaiting(view.Status) && (deadline is null || DateTimeOffset.UtcNow < deadline));
        return view;
    }

    private async Task<ActionView> GetActionAsync(string actionId, bool longPoll, CancellationToken cancellationToken)
    {
        var view = await SendAsync<ActionView>(
            HttpMethod.Get, $"/v1/actions/{Uri.EscapeDataString(actionId)}{(longPoll ? LongPoll : "")}", null, null, cancellationToken,
            longPoll ? LongPollWait : TimeSpan.Zero).ConfigureAwait(false);
        Observe(view);
        return view;
    }

    private void Observe(ActionView view)
    {
        if (view.FailMode is { } mode) _failMode.Value = mode;
    }

    private Decision ToDecision(ActionView view) => new(this)
    {
        ActionId = view.ActionId,
        Route = view.Route,
        Status = view.Status,
        HandoffId = view.HandoffId,
        Degraded = false,
        Reason = view.Reason,
        DecidedBy = view.DecidedBy,
        Findings = view.Findings ?? [],
        Tags = view.Tags ?? [],
        ExpiresAt = view.ExpiresAt,
    };

    /// <summary>Sends with retries: 3 attempts on 5xx or network errors, same body and Idempotency-Key; 4xx is never retried.</summary>
    // serverWait: extra time the server may hold the request (long-poll), added to the per-attempt timeout.
    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken cancellationToken, TimeSpan serverWait = default)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, Json.Options);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", RabbiXSdkInfo.UserAgent);
            if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_options.RequestTimeout + serverWait);

            HttpStatusCode statusCode;
            string text;
            try
            {
                using var response = await _http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                statusCode = response.StatusCode;
                text = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Network error or attempt timeout (not the caller's cancellation).
                if (attempt >= MaxAttempts)
                    throw new RabbiXUnavailableException($"RabbiX is unreachable ({method} {path}, {attempt} attempts).", null, ex);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if ((int)statusCode >= 500)
            {
                if (attempt >= MaxAttempts)
                    throw new RabbiXUnavailableException($"RabbiX returned {(int)statusCode} ({method} {path}, {attempt} attempts).", statusCode, null);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if ((int)statusCode is >= 200 and < 300)
                return JsonSerializer.Deserialize<T>(text, Json.Options)
                    ?? throw new RabbiXException($"RabbiX returned an empty body ({method} {path}).", statusCode);

            throw MapError(statusCode, text, method, path);
        }
    }

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = _options.RetryBackoff * Math.Pow(2, attempt - 1);
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }

    private static RabbiXException MapError(HttpStatusCode statusCode, string body, HttpMethod method, string path)
    {
        ProblemDetails? problem = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body)) problem = JsonSerializer.Deserialize<ProblemDetails>(body, Json.Options);
        }
        catch (JsonException)
        {
            // Not a problem+json body; fall back to the status code.
        }

        var message = $"{method} {path} failed with {(int)statusCode}: {problem?.Detail ?? problem?.Title ?? statusCode.ToString()}";
        return (int)statusCode switch
        {
            400 or 422 => new RabbiXValidationException(message, statusCode, problem?.Errors),
            401 or 403 => new RabbiXAuthenticationException(message, statusCode),
            404 => new RabbiXNotFoundException(message),
            409 => new RabbiXConflictException(message),
            _ => new RabbiXException(message, statusCode),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
