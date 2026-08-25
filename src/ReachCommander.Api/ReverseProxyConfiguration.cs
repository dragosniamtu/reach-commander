using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.HttpOverrides;

namespace ReachCommander.Api;

public static class ReverseProxyConfiguration
{
    public static IServiceCollection AddReachCommanderReverseProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            var knownProxies = configuration
                .GetSection("ReverseProxy:KnownProxies")
                .GetChildren()
                .Select(section => section.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(ResolveProxy)
                .Distinct();
            foreach (var proxy in knownProxies)
            {
                AddKnownProxy(options, proxy);
            }

            if (configuration.GetValue<bool>("ReverseProxy:TrustNetworkGateways"))
            {
                var gateways = ResolveNetworkGateways();
                if (gateways.Length == 0)
                {
                    throw new InvalidOperationException(
                        "ReverseProxy:TrustNetworkGateways is enabled, but no network gateway was found.");
                }

                foreach (var gateway in gateways)
                {
                    AddKnownProxy(options, gateway);
                }
            }
        });

        return services;
    }

    private static IPAddress[] ResolveNetworkGateways() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address)
            .Where(address =>
                !IPAddress.IsLoopback(address) &&
                !address.Equals(IPAddress.Any) &&
                !address.Equals(IPAddress.IPv6Any))
            .Distinct()
            .ToArray();

    private static IEnumerable<IPAddress> ResolveProxy(string? configuredValue)
    {
        var value = configuredValue?.Trim() ?? string.Empty;
        if (IPAddress.TryParse(value, out var address))
        {
            return [address];
        }

        if (Uri.CheckHostName(value) is not UriHostNameType.Dns)
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains an invalid IP address or host name: '{value}'.");
        }

        try
        {
            var addresses = Dns.GetHostAddresses(value);
            return addresses.Length > 0
                ? addresses
                : throw new InvalidOperationException(
                    $"ReverseProxy:KnownProxies host name did not resolve: '{value}'.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies host name did not resolve: '{value}'.",
                exception);
        }
    }

    private static void AddKnownProxy(
        ForwardedHeadersOptions options,
        IPAddress address)
    {
        AddIfMissing(options, address);
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            AddIfMissing(options, address.MapToIPv6());
        }
    }

    private static void AddIfMissing(
        ForwardedHeadersOptions options,
        IPAddress address)
    {
        if (!options.KnownProxies.Contains(address))
        {
            options.KnownProxies.Add(address);
        }
    }
}
