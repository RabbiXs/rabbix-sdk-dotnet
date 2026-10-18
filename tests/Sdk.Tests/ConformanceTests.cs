using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RabbiX.Sdk.Tests;

/// <summary>Runs the vendored conformance fixtures (see conformance/README.md) against a scripted HTTP handler.</summary>
public class ConformanceTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "conformance");

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static IEnumerable<string> Fixtures() =>
        Directory.GetFiles(FixtureDir, "*.json").Select(Path.GetFileName).Order().Cast<string>();

    [Test]
    public async Task All_fixtures_are_present()
    {
        await Assert.That(Fixtures().Count()).IsEqualTo(12);
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task Fixture(string file)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureDir, file)));
        var handler = new ScriptedHandler();
        using var client = new RabbiXClient(new HttpClient(handler), new RabbiXOptions
        {
            ApiKey = "rbx_test_conformance",
            BaseUrl = new Uri("http://conformance.test"),
            RetryBackoff = TimeSpan.Zero,
        });
        RabbiXSession? session = null;
        var failures = new List<string>();

        var index = 0;
        foreach (var call in doc.RootElement.GetProperty("calls").EnumerateArray())
        {
            var name = call.GetProperty("call").GetString()!;
            var label = $"call #{index++} ({name})";
            var args = call.GetProperty("args");
            handler.Load(call.GetProperty("exchanges"));

            object? result = null;
            Exception? error = null;
            try
            {
                switch (name)
                {
                    case "start_session":
                        session = await client.StartSessionAsync(Str(args, "goal")!, Str(args, "agent"), Opt(args, "metadata"));
                        result = new { session.SessionId };
                        break;
                    case "end_session":
                        var ended = await session!.EndAsync(Enum.Parse<SessionStatus>(Str(args, "status")!, ignoreCase: true), Str(args, "summary"));
                        result = new { Status = ended };
                        break;
                    case "propose":
                        result = await session!.ProposeAsync(
                            Str(args, "action")!,
                            args.GetProperty("proposal"),
                            Str(args, "reasoning"),
                            Opt(args, "evidence"),
                            args.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(t => t.GetString()!).ToList() : null,
                            Str(args, "callback_url"),
                            waitForApproval: Bool(args, "wait_for_approval"));
                        break;
                    case "get_decision":
                        result = await client.GetDecisionAsync(Str(args, "action_id")!, wait: Bool(args, "wait"));
                        break;
                    case "report":
                        var status = await client.ReportAsync(Str(args, "action_id")!, Bool(args, "success"), Opt(args, "output"), Str(args, "error"));
                        result = new { Status = status };
                        break;
                    default:
                        throw new NotSupportedException($"Unknown call '{name}'.");
                }
            }
            catch (RabbiXException ex)
            {
                error = ex;
            }

            failures.AddRange(handler.Failures.Select(f => $"{label}: {f}"));
            handler.Failures.Clear();
            if (handler.Remaining > 0) failures.Add($"{label}: {handler.Remaining} expected request(s) not made");

            var expect = call.GetProperty("expect");
            if (expect.TryGetProperty("result", out var expectedResult))
            {
                if (error is not null) failures.Add($"{label}: expected a result, got {error.GetType().Name}: {error.Message}");
                else Subset(expectedResult, JsonSerializer.SerializeToElement(result, ResultJson), $"{label} result", failures);
            }
            else
            {
                var kind = expect.GetProperty("error").GetString();
                var actualKind = error is null ? null : ErrorKind(error);
                if (actualKind != kind) failures.Add($"{label}: expected error '{kind}', got {actualKind ?? "a result"} {error?.Message}");
                else if (expect.TryGetProperty("fields", out var fields))
                    Subset(fields, JsonSerializer.SerializeToElement(ErrorFields(error!), ResultJson), $"{label} error fields", failures);
            }
        }

        if (failures.Count > 0) Assert.Fail(string.Join(Environment.NewLine, failures));
    }

    private static string ErrorKind(Exception ex) => ex switch
    {
        HandedOffException => "handed_off",
        RejectedException => "rejected",
        ExpiredException => "expired",
        DeclinedException => "declined",
        RabbiXValidationException => "validation",
        RabbiXAuthenticationException => "authentication",
        RabbiXNotFoundException => "not_found",
        RabbiXConflictException => "conflict",
        RabbiXUnavailableException => "unavailable",
        _ => ex.GetType().Name,
    };

    private static object ErrorFields(Exception ex) => ex switch
    {
        HandedOffException h => new { h.ActionId, h.HandoffId },
        RabbiXDecisionException d => new { d.ActionId },
        _ => new { },
    };

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static bool Bool(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.GetBoolean();

    private static object? Opt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>README rule: every key in <paramref name="expected"/> present with an equal value; objects recursively, arrays exactly.</summary>
    internal static void Subset(JsonElement expected, JsonElement actual, string path, List<string> failures)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"{path}: expected object, got {actual.GetRawText()}");
                return;
            }
            foreach (var prop in expected.EnumerateObject())
            {
                if (actual.TryGetProperty(prop.Name, out var value)) Subset(prop.Value, value, $"{path}.{prop.Name}", failures);
                else failures.Add($"{path}.{prop.Name}: missing (expected {prop.Value.GetRawText()})");
            }
        }
        else if (!JsonEquals(expected, actual))
        {
            failures.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
        }
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && JsonEquals(p.Value, v)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(x => JsonEquals(x.First, x.Second)),
            JsonValueKind.Number => a.GetDecimal() == b.GetDecimal(),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => true,
        };
    }

    /// <summary>Answers each request with the next scripted exchange and records every mismatch.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<JsonElement> _exchanges = new();
        private readonly Dictionary<string, string> _captures = new(StringComparer.Ordinal);

        public List<string> Failures { get; } = [];

        public int Remaining => _exchanges.Count;

        public void Load(JsonElement exchanges)
        {
            _exchanges.Clear();
            foreach (var exchange in exchanges.EnumerateArray()) _exchanges.Enqueue(exchange.Clone());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var actual = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            if (!_exchanges.TryDequeue(out var exchange))
            {
                Failures.Add($"unexpected request {actual}");
                throw new InvalidOperationException($"Unexpected request {actual}");
            }

            var expected = exchange.GetProperty("request");
            var expectedLine = $"{expected.GetProperty("method").GetString()} {expected.GetProperty("path").GetString()}";
            if (actual != expectedLine) Failures.Add($"expected request {expectedLine}, got {actual}");

            if (expected.TryGetProperty("headers", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                    CheckHeader(request, header.Name, header.Value.GetString()!);
            }

            if (expected.TryGetProperty("body", out var expectedBody))
            {
                var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                if (text.Length == 0) Failures.Add($"{actual}: expected a JSON body, got none");
                else
                {
                    using var body = JsonDocument.Parse(text);
                    Subset(expectedBody, body.RootElement, $"{actual} body", Failures);
                }
            }

            var response = exchange.GetProperty("response");
            if (response.TryGetProperty("network_error", out var networkError) && networkError.GetBoolean())
                throw new HttpRequestException("Simulated network error (connection refused).");

            var message = new HttpResponseMessage((HttpStatusCode)response.GetProperty("status").GetInt32());
            if (response.TryGetProperty("body", out var responseBody) && responseBody.ValueKind != JsonValueKind.Null)
                message.Content = new StringContent(responseBody.GetRawText(), Encoding.UTF8, "application/json");
            return message;
        }

        private void CheckHeader(HttpRequestMessage request, string name, string expected)
        {
            var value = request.Headers.TryGetValues(name, out var values) || (request.Content?.Headers.TryGetValues(name, out values) ?? false)
                ? string.Join(",", values!)
                : null;

            if (expected == "*")
            {
                if (string.IsNullOrEmpty(value)) Failures.Add($"header {name}: expected present, was missing");
            }
            else if (expected.StartsWith("$capture:", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(value)) Failures.Add($"header {name}: expected present ({expected}), was missing");
                else if (!_captures.TryAdd(expected, value) && _captures[expected] != value)
                    Failures.Add($"header {name}: expected {_captures[expected]} ({expected}), got {value}");
            }
            else if (value != expected)
            {
                Failures.Add($"header {name}: expected '{expected}', got '{value}'");
            }
        }
    }
}
