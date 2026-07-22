using System.Net;

namespace FabrCore.Host.Configuration;

internal static class GatewayUriUtilities
{
    public static bool TryParse(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            string.Equals(parsed.Scheme, "gwy.tcp", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(parsed.Host) &&
            parsed.Port is > 0 and <= 65535 &&
            parsed.AbsolutePath == "/0" &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment) &&
            IsUsableHost(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    public static Uri Create(IPAddress address, int port)
    {
        var builder = new UriBuilder("gwy.tcp", address.ToString(), port, "0");
        return builder.Uri;
    }

    public static bool IsUsableAddress(IPAddress address)
        => !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    private static bool IsUsableHost(string host)
        => !IPAddress.TryParse(host, out var address) || IsUsableAddress(address);
}
