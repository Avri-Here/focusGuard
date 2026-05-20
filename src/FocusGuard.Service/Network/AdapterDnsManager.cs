using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Network;

/// <summary>
/// Pushes <c>127.0.0.1</c> onto every active adapter so the local DNS sinkhole intercepts every
/// query the machine makes. Originals are remembered the first time
/// <see cref="OverrideToLoopback"/> is called so a subsequent Disable / uninstall can put them
/// back even if the service has been restarted in-between (the caller is responsible for
/// persisting the returned map).
/// </summary>
public sealed class AdapterDnsManager : IAdapterDnsManager
{
    private static readonly IReadOnlyList<string> Loopback = new[] { "127.0.0.1" };

    private readonly INetworkAdapterBackend _backend;
    private readonly ILogger<AdapterDnsManager> _logger;
    private readonly Dictionary<string, IReadOnlyList<string>> _firstSeenOriginals = new();
    private bool _hasOverridden;

    public AdapterDnsManager(INetworkAdapterBackend backend, ILogger<AdapterDnsManager> logger)
    {
        _backend = backend;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideToLoopback()
    {
        var adapters = _backend.EnumerateActive();

        if (!_hasOverridden)
        {
            foreach (var a in adapters)
            {
                _firstSeenOriginals[a.Id] = a.CurrentDnsServers.ToArray();
            }
            _hasOverridden = true;
        }
        else
        {
            // Pick up adapters that appeared after the first override — record their originals
            // before we stomp them.
            foreach (var a in adapters)
            {
                if (!_firstSeenOriginals.ContainsKey(a.Id) && !DnsListIsLoopback(a.CurrentDnsServers))
                {
                    _firstSeenOriginals[a.Id] = a.CurrentDnsServers.ToArray();
                }
            }
        }

        foreach (var a in adapters)
        {
            if (DnsListIsLoopback(a.CurrentDnsServers)) continue;
            _backend.SetDnsServers(a.Id, Loopback);
            _logger.LogInformation("AdapterDnsManager: pushed loopback DNS onto {Adapter}", a.Id);
        }

        return _firstSeenOriginals.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public void Restore(IReadOnlyDictionary<string, IReadOnlyList<string>> originals)
    {
        var present = _backend.EnumerateActive().ToDictionary(a => a.Id);
        foreach (var (id, original) in originals)
        {
            if (!present.ContainsKey(id))
            {
                _logger.LogInformation("AdapterDnsManager: skipping restore for missing adapter {Adapter}", id);
                continue;
            }
            _backend.SetDnsServers(id, original);
            _logger.LogInformation("AdapterDnsManager: restored DNS on {Adapter} ({Count} server(s))", id, original.Count);
        }
        _hasOverridden = false;
        _firstSeenOriginals.Clear();
    }

    private static bool DnsListIsLoopback(IReadOnlyList<string> dns)
        => dns.Count == 1 && string.Equals(dns[0], "127.0.0.1", StringComparison.Ordinal);
}
