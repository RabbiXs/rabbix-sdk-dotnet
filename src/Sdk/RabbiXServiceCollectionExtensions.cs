using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RabbiX.Sdk;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="RabbiXClient"/> as a typed client of <c>IHttpClientFactory</c>.</summary>
public static class RabbiXServiceCollectionExtensions
{
    /// <summary>Registers <see cref="RabbiXClient"/>, binding <see cref="RabbiXOptions"/> from <paramref name="configuration"/> (e.g. <c>config.GetSection("RabbiX")</c>).</summary>
    /// <returns>The HTTP client builder, to add handlers or configure the primary handler.</returns>
    public static IHttpClientBuilder AddRabbiX(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RabbiXOptions>().Bind(configuration);
        return services.AddRabbiXClient();
    }

    /// <summary>Registers <see cref="RabbiXClient"/>, configuring <see cref="RabbiXOptions"/> in code.</summary>
    /// <returns>The HTTP client builder, to add handlers or configure the primary handler.</returns>
    public static IHttpClientBuilder AddRabbiX(this IServiceCollection services, Action<RabbiXOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<RabbiXOptions>().Configure(configure);
        return services.AddRabbiXClient();
    }

    private static IHttpClientBuilder AddRabbiXClient(this IServiceCollection services)
    {
        // Typed clients are transient; the cached fail mode must outlive them.
        services.TryAddSingleton<FailModeCache>();
        return services
            .AddHttpClient(nameof(RabbiXClient))
            // Per-attempt timeouts (incl. long-poll) are enforced by the client from RabbiXOptions.RequestTimeout.
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddTypedClient((http, sp) => new RabbiXClient(
                http, sp.GetRequiredService<IOptions<RabbiXOptions>>().Value, sp.GetRequiredService<FailModeCache>()));
    }
}
