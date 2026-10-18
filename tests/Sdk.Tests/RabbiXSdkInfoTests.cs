namespace RabbiX.Sdk.Tests;

public class RabbiXSdkInfoTests
{
    [Test]
    public async Task User_agent_carries_the_package_version()
    {
        await Assert.That(RabbiXSdkInfo.UserAgent).IsEqualTo("rabbix-sdk-dotnet/0.1.0");
    }
}
