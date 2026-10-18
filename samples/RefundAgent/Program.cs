// Refund agent sample: proposes three refunds and shows each route.
//
//   RABBIX_API_KEY=rbx_test_... RABBIX_BASE_URL=http://localhost:5021 dotnet run --project samples/RefundAgent
//   dotnet run --project samples/RefundAgent -- resume <action-id>     # resume a pending action later

using RabbiX.Sdk;

var apiKey = Environment.GetEnvironmentVariable("RABBIX_API_KEY")
    ?? throw new InvalidOperationException("Set RABBIX_API_KEY.");
var options = new RabbiXOptions { ApiKey = apiKey };
if (Environment.GetEnvironmentVariable("RABBIX_BASE_URL") is { Length: > 0 } baseUrl) options.BaseUrl = new Uri(baseUrl);

using var rbx = new RabbiXClient(options);

if (args is ["resume", var actionId])
{
    var decision = await rbx.GetDecisionAsync(actionId);
    Console.WriteLine($"{actionId}: {decision.Status} ({decision.Reason})");
    if (decision.Approved)
    {
        var refundId = IssueRefund(1800m);
        Console.WriteLine($"  committed {refundId}, reported: {await decision.ReportAsync(success: true, output: new { refund_id = refundId })}");
    }
    return;
}

await using var session = await rbx.StartSessionAsync(goal: "Process today's refund requests", agent: "refund-agent");
Console.WriteLine($"session {session.SessionId}");

foreach (var amount in new[] { 100m, 1800m, 25000m })
{
    var proposal = new { customer_id = "C-4411", order_id = $"O-{amount}", amount, currency = "USD" };
    try
    {
        // 1800 is proposed non-blocking: the agent stores the action id and resumes later.
        var decision = await session.ProposeAsync(
            "refunds.issue",
            proposal,
            reasoning: "Item arrived damaged; customer has no previous refunds.",
            evidence: new Dictionary<string, object> { ["customer.refund_count"] = 0, ["ticket.attachments"] = new[] { "damage.jpg" } },
            tags: ["support"],
            waitForApproval: amount != 1800m);

        if (decision.Status == ActionStatus.Pending)
        {
            Console.WriteLine($"{amount,8}: {decision.Route} pending, resume later with: resume {decision.ActionId}");
            continue;
        }

        var refundId = IssueRefund(amount);                                      // the commit
        var status = await decision.ReportAsync(success: true, output: new { refund_id = refundId });
        Console.WriteLine($"{amount,8}: {decision.Route} {decision.Status} -> committed {refundId}, reported {status}");
    }
    catch (HandedOffException h)
    {
        Console.WriteLine($"{amount,8}: handed off to a human (action {h.ActionId}, handoff {h.HandoffId}); not committed");
    }
    catch (RejectedException r)
    {
        Console.WriteLine($"{amount,8}: rejected ({r.Decision.Reason}); not committed");
    }
}

static string IssueRefund(decimal amount) => $"R-{amount}-{Guid.NewGuid().ToString()[..8]}";
