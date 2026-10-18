using System.Text.Json;
using System.Text.Json.Serialization;

namespace RabbiX.Sdk;

internal static class Json
{
    /// <summary>snake_case properties; dictionary keys (e.g. evidence) are sent as given.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new SnakeCaseEnumConverter<Route>(),
            new SnakeCaseEnumConverter<ActionStatus>(),
            new SnakeCaseEnumConverter<SessionStatus>(),
            new SnakeCaseEnumConverter<FailMode>(),
        },
    };
}

/// <summary>snake_case enum strings; unknown values read as the default member so newer APIs don't break older SDKs.</summary>
internal sealed class SnakeCaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> ToName =
        Enum.GetValues<T>().ToDictionary(v => v, v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

    private static readonly Dictionary<string, T> FromName = ToName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && FromName.TryGetValue(reader.GetString()!, out var value) ? value : default;

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToName[value]);
}

internal sealed record StartSessionRequest(string Goal, string? Agent, object? Metadata);

internal sealed record StartSessionResponse(string SessionId);

internal sealed record EndSessionRequest(SessionStatus Status, string? Summary);

internal sealed record EndSessionResponse(string SessionId, SessionStatus Status);

internal sealed record ProposeRequest(
    string SessionId, string Action, object Proposal, string? Reasoning, object? Evidence, IReadOnlyList<string>? Tags, string? CallbackUrl);

internal sealed record ReportRequest(bool Success, object? Output, string? Error);

internal sealed record ReportResponse(string ActionId, ActionStatus Status);

internal sealed record ActionView(
    string? ActionId,
    Route? Route,
    ActionStatus Status,
    string? Reason,
    string? DecidedBy,
    IReadOnlyList<Finding>? Findings,
    IReadOnlyList<string>? Tags,
    string? HandoffId,
    DateTimeOffset? ExpiresAt,
    FailMode? FailMode);

internal sealed record ProblemDetails(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
