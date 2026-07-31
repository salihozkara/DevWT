using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace Devwt.Service;

public sealed record DevwtGatewayWorkerEndpoint(string Ip, int Port)
{
    public static DevwtGatewayWorkerEndpoint FromRoute(GatewayRoute route) =>
        new(NormalizeIp(route.ListenIp), route.Port);

    public static DevwtGatewayWorkerEndpoint FromListenEndpoint(GatewayListenEndpoint endpoint) =>
        new(NormalizeIp(endpoint.Ip), endpoint.Port);

    private static string NormalizeIp(string ip) =>
        IPAddress.TryParse(ip, out var address) ? address.ToString().ToUpperInvariant() : ip.ToUpperInvariant();
}

public static class DevwtGatewayWorkerExitPolicy
{
    public static IReadOnlyList<int> ListenerProcessIdsFor(
        IReadOnlyList<GatewayRoute> routes,
        DevwtGatewayWorkerEndpoint endpoint) =>
        routes
            .Where(route => DevwtGatewayWorkerEndpoint.FromRoute(route) == endpoint)
            .Select(route => route.ListenerProcessId)
            .Distinct()
            .Order()
            .ToArray();
}

public static class DevwtGatewayWorkerNames
{
    public const string AliasMarker = "--DevWT-Proxy--";

    public static string BuildAliasFileName(IReadOnlyList<string> imageNames, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var readableName = string.Join('+', imageNames.Select(SanitizeImageName));
        if (readableName.Length > 120)
        {
            readableName = readableName[..120];
        }

        if (string.IsNullOrWhiteSpace(readableName))
        {
            readableName = "unknown";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..12];
        return $"{readableName}{AliasMarker}{hash}.exe";
    }

    private static string SanitizeImageName(string imageName)
    {
        var name = Path.GetFileNameWithoutExtension(imageName);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
