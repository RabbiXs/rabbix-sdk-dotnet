# RabbiX SDK for .NET

Govern what your autonomous AI agents commit: propose an effect, get a route from RabbiX, commit only when allowed. Built on `HttpClient` and `Microsoft.Extensions.*`.

```sh
dotnet add package RabbiX.Sdk   # not published yet
```

## Status

Pre-release (0.1.0). The client is implemented against the RabbiX agent API and passes the shared conformance suite. It is not on NuGet yet. Do not use in production yet.

## Usage

```csharp
using RabbiX.Sdk;

using var rbx = new RabbiXClient(new RabbiXOptions { ApiKey = Environment.GetEnvironmentVariable("RABBIX_API_KEY")! });

await using var session = await rbx.StartSessionAsync(goal: $"Decide whether {customer.Id} gets a refund", agent: "refund-agent");
try
{
    var decision = await session.ProposeAsync(
        "refunds.issue",
        new { customer_id = customer.Id, order_id = order.Id, amount = 350.00m, currency = "USD" },
        reasoning: "New customer, no previous refunds, item arrived damaged (photo attached).",
        evidence: new Dictionary<string, object> { ["customer.refund_count"] = 0 },
        tags: ["support"],
        waitForApproval: true);   // human_in_the_loop: long-polls until a human decides

    var refund = await payments.RefundAsync(/* ... */);                  // the commit
    await decision.ReportAsync(success: true, output: new { refund_id = refund.Id });
}
catch (HandedOffException h)
{
    SavePending(h.ActionId);   // do not commit; resume with rbx.GetDecisionAsync(actionId) if a human releases it
}
catch (RejectedException) { }
```

Hosted apps register a typed `HttpClient` through `IHttpClientFactory`, then inject `RabbiXClient`:

```csharp
builder.Services.AddRabbiX(builder.Configuration.GetSection("RabbiX"));   // ApiKey, BaseUrl, RetryBackoff, RequestTimeout
```

| Call | Returns / throws |
| --- | --- |
| `rbx.StartSessionAsync(goal, agent?, metadata?)` | `RabbiXSession`. Disposing it ends the session as `completed` unless you already called `EndAsync(status, summary)`. Dispose cannot see exceptions, so on failure call `EndAsync(SessionStatus.Failed)` yourself. A session is never ended twice. |
| `session.ProposeAsync(action, proposal, reasoning?, evidence?, tags?, callbackUrl?, waitForApproval, idempotencyKey?, waitTimeout?)` | `Decision` (`ActionId`, `Route`, `Status`, `HandoffId`, `Degraded`, `Reason`, `Findings`, `Tags`, `ExpiresAt`, `Approved`). Throws `HandedOffException` (always on `human_only`), `RejectedException`, `ExpiredException`, `DeclinedException`, `WaitTimeoutException`. Without `waitForApproval`, it returns a `pending` decision instead of waiting. |
| `rbx.GetDecisionAsync(actionId, wait, waitTimeout?)` | `Decision`. Never throws because of the decision's status. Check `Approved` (approved or released) before you commit. |
| `decision.ReportAsync(success, output?, error?)` or `rbx.ReportAsync(actionId, ...)` | The new action status. Does nothing for a degraded decision. |

HTTP errors map to `RabbiXValidationException` (400/422), `RabbiXAuthenticationException` (401/403), `RabbiXNotFoundException` (404) and `RabbiXConflictException` (409). All SDK errors derive from `RabbiXException`.

**Retries and fail mode.** Each request, long-poll requests included, is tried up to 3 times on a 5xx or network error. Retries reuse the same `Idempotency-Key`. 4xx responses are never retried. If RabbiX stays unreachable, `ProposeAsync` applies your organization's fail mode, which the SDK caches from the latest decision response (default: closed):

- **Closed:** it throws `RabbiXUnavailableException`.
- **Open:** it returns a degraded approval (`Degraded = true`, `ActionId = null`).

Fail-open covers only the proposal request itself. If RabbiX becomes unreachable while the SDK waits for a human, `ProposeAsync` throws `RabbiXUnavailableException` carrying the `ActionId`. All other calls always throw `RabbiXUnavailableException`.

A runnable example is in [samples/RefundAgent](samples/RefundAgent/Program.cs).

## Development

```sh
dotnet build RabbiX.Sdk.slnx
dotnet test --solution RabbiX.Sdk.slnx
```

`conformance/` holds a vendored copy of the shared SDK conformance fixtures; the source of truth is the RabbiX platform repository. `tests/Sdk.Tests/ConformanceTests.cs` runs each fixture against a scripted HTTP handler.

## How it works

Your agent works freely. Before its work takes effect (a refund is paid, an email sent, code pushed), it **proposes** that effect to RabbiX together with its goal, reasoning and evidence. RabbiX answers with a route:

| Route | What the SDK does |
| --- | --- |
| `ai_autonomous` | Returns; your code commits. |
| `human_in_the_loop` | Blocks (or returns a pending decision) until a human approves or rejects. |
| `human_only` | Throws `HandedOffException`. Your code must not commit; a human takes over. If they approve, RabbiX releases the commit later. |

## What this SDK sends, and when

This repository is public so your security reviewers can verify this list against the code. The SDK sends **only what your code explicitly passes to it**, plus the transport metadata listed below. It does not read environment variables (other than the API key you point it to), files, process or host information, and it has no telemetry of its own.

| Call | When | Fields sent |
| --- | --- | --- |
| Start session | Your code starts a session | `goal`, optional `agent` (name), optional `metadata` |
| Propose | Before your code commits an effect | `session_id`, `action` (name), `proposal`, optional `reasoning`, optional `evidence`, optional `tags`, optional `callback_url` |
| Wait / get decision | While waiting for a human decision, or when your code resumes | `action_id` (in the URL) |
| Report result | After your code committed | `action_id`, `success`, `output` and/or `error` as passed |
| End session | Your code ends the session, or disposes it | `session_id`, `status`, optional `summary` |

Transport metadata: `Authorization: Bearer <your API key>` and `User-Agent` (SDK name and version) on every request. `Idempotency-Key` on proposals only; it is random unless you pass one, and retries reuse it. `traceparent` only if your process already has an active trace.

All requests go over HTTPS to the RabbiX API endpoint you configure. Nothing is sent anywhere else.

**Redaction**: you decide what goes into `proposal`, `reasoning` and `evidence`. Leave out or mask fields you are not allowed to share; a redaction hook will let you do this centrally.

## License

Apache-2.0
