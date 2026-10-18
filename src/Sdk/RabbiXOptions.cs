namespace RabbiX.Sdk;

/// <summary>Configuration for <see cref="RabbiXClient"/>. Bindable from configuration (section <c>RabbiX</c>).</summary>
public sealed class RabbiXOptions
{
    /// <summary>API key (<c>rbx_live_…</c> / <c>rbx_test_…</c>), sent as <c>Authorization: Bearer</c>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>RabbiX API endpoint. Defaults to <c>https://api.rabbix.ai</c>.</summary>
    public Uri BaseUrl { get; set; } = new("https://api.rabbix.ai");

    /// <summary>
    /// Delay before the first retry of a failed request (5xx or network error); doubles per retry.
    /// <see cref="TimeSpan.Zero"/> retries immediately. Requests are attempted 3 times in total.
    /// </summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Timeout of a single HTTP attempt (default 30 s). Long-poll requests get this on top of the 30 s server wait.
    /// The SDK enforces it itself; an <see cref="HttpClient"/> you pass in must have a larger <see cref="HttpClient.Timeout"/> (the 100 s default is fine).
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
