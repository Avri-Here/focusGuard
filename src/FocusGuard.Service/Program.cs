using System.Runtime.Versioning;
using FocusGuard.Core;
using FocusGuard.Core.Security;
using FocusGuard.Service;
using FocusGuard.Service.Network;

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
builder.Services.AddSingleton<IFirewallManager, NoOpFirewallManager>();
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
