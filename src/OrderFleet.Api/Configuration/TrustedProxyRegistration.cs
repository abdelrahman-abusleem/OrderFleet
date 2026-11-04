using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace OrderFleet.Api.Configuration;

public sealed class TrustedProxyOptions
{
    public bool Enabled { get; set; }
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];

    public bool IsValid() => KnownProxies is not null && KnownNetworks is not null
        && KnownProxies.All(x => IPAddress.TryParse(x, out _))
        && KnownNetworks.All(x => IPNetwork.TryParse(x, out var network) && network.PrefixLength > 0)
        && (!Enabled || KnownProxies.Length + KnownNetworks.Length > 0);
}

public static class TrustedProxyRegistration
{
    public static void AddTrustedProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TrustedProxyOptions>().Bind(configuration.GetSection("TrustedProxy"))
            .Validate(x => x.IsValid(), "Invalid trusted proxy configuration.").ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<TrustedProxyOptions>>((forwarded, configured) =>
        {
            var settings = configured.Value;


            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            forwarded.ForwardLimit = 1;
            forwarded.ForwardedHeaders = settings.Enabled
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            foreach (var address in settings.KnownProxies) forwarded.KnownProxies.Add(IPAddress.Parse(address));
            foreach (var network in settings.KnownNetworks) forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
        });
    }
}
