using System.Net;
using System.Runtime.Versioning;
using FocusGuard.Core;
using FocusGuard.Core.Security;
using FocusGuard.Service;
using FocusGuard.Service.Network;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection("FocusGuard"));

builder.Services.AddWindowsService(o =>
{
    o.ServiceName = "FocusGuard";
});

builder.Logging.AddEventLog(o =>
{
    o.SourceName = "FocusGuard";
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<IFirewallManager, FirewallManager>();
builder.Services.AddSingleton<INetworkAdapterBackend, WmiNetworkAdapterBackend>();
builder.Services.AddSingleton<IAdapterDnsManager, AdapterDnsManager>();
builder.Services.AddSingleton<IUpstreamResolver>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<ServiceOptions>>().Value;
    var endpoints = opts.UpstreamDns.Select(host => new IPEndPoint(IPAddress.Parse(host), opts.UpstreamDnsPort));
    return new DnsClientUpstreamResolver(endpoints,
        sp.GetRequiredService<ILogger<DnsClientUpstreamResolver>>());
});
builder.Services.AddSingleton<IDnsSinkhole>(sp =>
{
    var configStore = sp.GetRequiredService<IObjectStore<FocusGuardConfig>>();
    return new DnsSinkhole(
        sp.GetRequiredService<IFirewallManager>(),
        sp.GetRequiredService<IUpstreamResolver>(),
        sp.GetRequiredService<IClock>(),
        () => configStore.Load()?.Whitelist ?? new List<string>(),
        sp.GetRequiredService<ILogger<DnsSinkhole>>());
});
builder.Services.AddSingleton(StoreFactory.CreateConfigStore);
builder.Services.AddSingleton(StoreFactory.CreateStateStore);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

[SupportedOSPlatform("windows")]
internal static class StoreFactory
{
    public static IObjectStore<FocusGuardConfig> CreateConfigStore(IServiceProvider sp)
    {
        var opts = ResolveOptions(sp);
        Directory.CreateDirectory(opts.DataDirectory);
        return new DpapiStore<FocusGuardConfig>(opts.ConfigPath);
    }

    public static IObjectStore<FocusGuardState> CreateStateStore(IServiceProvider sp)
    {
        var opts = ResolveOptions(sp);
        Directory.CreateDirectory(opts.DataDirectory);
        return new DpapiStore<FocusGuardState>(opts.StatePath);
    }

    private static ServiceOptions ResolveOptions(IServiceProvider sp) =>
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceOptions>>().Value;
}
