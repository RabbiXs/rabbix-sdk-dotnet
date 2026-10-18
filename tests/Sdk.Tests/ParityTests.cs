using System.Net;
using System.Text;

namespace RabbiX.Sdk.Tests;

/// <summary>Cross-SDK parity rules from conformance/README.md not pinned by a fixture.</summary>
public class ParityTests
{
    private const string Session = """{"session_id":"s1"}""";

    private static string View(string status, string failMode = "closed") =>
        $$"""{"action_id":"a1","route":"human_in_the_loop","status":"{{status}}","reason":null,"decided_by":"policy","findings":[],"tags":[],"handoff_id":null,"expires_at":null,"fail_mode":"{{failMode}}"}""";

    private static (RabbiXClient Client, Handler Handler) Create(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new Handler(responses);
        return (new RabbiXClient(new HttpClient(handler), new RabbiXOptions { ApiKey = "k", RetryBackoff = TimeSpan.Zero }), handler);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Down(HttpRequestMessage _) => throw new HttpRequestException("down");

    [Test]
    public async Task Unreachable_long_poll_raises_unavailable_with_action_id_even_when_fail_open()
    {
        var (client, _) = Create(Ok(Session), Ok(View("pending", "open"), HttpStatusCode.Accepted), Down, Down, Down);
        var session = await client.StartSessionAsync("g");
        var ex = await Assert.That(async () => { await session.ProposeAsync("x", new { }, waitForApproval: true); }).Throws<RabbiXUnavailableException>();
        await Assert.That(ex!.ActionId).IsEqualTo("a1");
    }

    [Test]
    public async Task Wait_timeout_raises_in_propose_and_returns_pending_in_get_decision()
    {
        var (client, _) = Create(
            Ok(Session), Ok(View("pending"), HttpStatusCode.Accepted), Ok(View("pending"), HttpStatusCode.Accepted),
            Ok(View("pending"), HttpStatusCode.Accepted));
        var session = await client.StartSessionAsync("g");
        var ex = await Assert.That(async () => { await session.ProposeAsync("x", new { }, waitForApproval: true, waitTimeout: TimeSpan.Zero); })
            .Throws<WaitTimeoutException>();
        await Assert.That(ex!.ActionId).IsEqualTo("a1");

        var decision = await client.GetDecisionAsync("a1", wait: true, waitTimeout: TimeSpan.Zero);
        await Assert.That(decision.Status).IsEqualTo(ActionStatus.Pending);
        await Assert.That(decision.Approved).IsFalse();
    }

    [Test]
    [Arguments("rejected")]
    [Arguments("expired")]
    [Arguments("declined")]
    [Arguments("handed_off")]
    public async Task Get_decision_never_raises_on_status(string status)
    {
        var (client, _) = Create(Ok(View(status)));
        var decision = await client.GetDecisionAsync("a1");
        await Assert.That(decision.Approved).IsFalse();
    }

    [Test]
    [Arguments("committed")]
    [Arguments("failed")]
    [Arguments("done_by_human")]
    public async Task Propose_returns_terminal_statuses(string status)
    {
        var (client, _) = Create(Ok(Session), Ok(View(status)));
        var session = await client.StartSessionAsync("g");
        var decision = await session.ProposeAsync("x", new { });
        await Assert.That(decision.Approved).IsFalse();
    }

    [Test]
    public async Task Degraded_report_is_a_no_op()
    {
        var (client, handler) = Create(Ok(Session), Ok(View("approved", "open")), Down, Down, Down);
        var session = await client.StartSessionAsync("g");
        await session.ProposeAsync("x", new { });
        var degraded = await session.ProposeAsync("x", new { });
        await Assert.That(degraded.Degraded).IsTrue();
        await Assert.That(degraded.Approved).IsTrue();

        await degraded.ReportAsync(success: true);
        await Assert.That(handler.Requests).IsEqualTo(5);
    }

    [Test]
    public async Task Session_ends_once()
    {
        var ended = """{"session_id":"s1","status":"failed"}""";
        var (client, handler) = Create(Ok(Session), Ok(ended));
        await using (var session = await client.StartSessionAsync("g"))
        {
            await session.EndAsync(SessionStatus.Failed);
            await session.EndAsync(SessionStatus.Failed);
        }
        await Assert.That(handler.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task Disposing_ends_completed()
    {
        var (client, handler) = Create(Ok(Session), Ok("""{"session_id":"s1","status":"completed"}"""));
        await using (await client.StartSessionAsync("g"))
        {
        }
        await Assert.That(handler.LastBody).Contains("\"status\":\"completed\"");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Requests >= responses.Length) throw new InvalidOperationException($"Unexpected request {request.RequestUri}");
            return responses[Requests++](request);
        }
    }
}
