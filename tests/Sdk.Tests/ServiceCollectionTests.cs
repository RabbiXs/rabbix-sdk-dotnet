using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RabbiX.Sdk.Tests;

public class ServiceCollectionTests
{
    [Test]
    public async Task AddRabbiX_binds_options_from_configuration_and_resolves_the_client()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbiX:ApiKey"] = "rbx_test_di",
                ["RabbiX:BaseUrl"] = "http://localhost:5021",
                ["RabbiX:RetryBackoff"] = "00:00:00",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddRabbiX(config.GetSection("RabbiX"));
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RabbiXOptions>>().Value;
        await Assert.That(options.ApiKey).IsEqualTo("rbx_test_di");
        await Assert.That(options.BaseUrl).IsEqualTo(new Uri("http://localhost:5021"));
        await Assert.That(options.RetryBackoff).IsEqualTo(TimeSpan.Zero);

        var client = provider.GetRequiredService<RabbiXClient>();
        await Assert.That(client.FailMode).IsEqualTo(FailMode.Closed);
    }

    [Test]
    public async Task Client_requires_an_api_key()
    {
        await Assert.That(() => new RabbiXClient(new HttpClient(), new RabbiXOptions())).Throws<ArgumentException>();
    }

    [Test]
    public async Task Calls_other_than_propose_raise_unavailable_after_three_attempts()
    {
        var handler = new FailingHandler();
        using var client = new RabbiXClient(new HttpClient(handler), new RabbiXOptions { ApiKey = "k", RetryBackoff = TimeSpan.Zero });
        await Assert.That(() => client.ReportAsync("a", true)).Throws<RabbiXUnavailableException>();
        await Assert.That(handler.Calls).IsEqualTo(3);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("down");
        }
    }
}
