using System.Reflection;

namespace RabbiX.Sdk;

/// <summary>Identifies this SDK to the RabbiX API.</summary>
public static class RabbiXSdkInfo
{
    /// <summary>Sent as <c>User-Agent</c> on every request (see README: what this SDK sends).</summary>
    public static string UserAgent { get; } =
        $"rabbix-sdk-dotnet/{typeof(RabbiXSdkInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]}";
}
