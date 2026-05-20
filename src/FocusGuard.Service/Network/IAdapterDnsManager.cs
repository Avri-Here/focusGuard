namespace FocusGuard.Service.Network;

/// <summary>One adapter, identified by a stable string id (GUID for the WMI backend).</summary>
public sealed record NetworkAdapterInfo(string Id, IReadOnlyList<string> CurrentDnsServers);

/// <summary>
/// Backend abstraction over Win32_NetworkAdapterConfiguration so the manager logic stays
/// unit-testable. Production: <see cref="WmiNetworkAdapterBackend"/>.
/// </summary>
public interface INetworkAdapterBackend
{
    /// <summary>Enumerate IP-enabled adapters. Each call returns the *current* DNS configuration.</summary>
    IReadOnlyList<NetworkAdapterInfo> EnumerateActive();

    /// <summary>Set the DNS server search order on a specific adapter. Empty list = revert to DHCP.</summary>
    void SetDnsServers(string adapterId, IReadOnlyList<string> dnsServers);
}

/// <summary>
/// Pushes <c>127.0.0.1</c> onto every active adapter's DNS server list at start, saves the
/// originals so admin Disable / uninstall can restore them. Production lives at
/// <see cref="AdapterDnsManager"/> with a WMI backend.
/// </summary>
public interface IAdapterDnsManager
{
    /// <summary>
    /// Force every active adapter to use the loopback resolver. Returns a map of adapter-id →
    /// original DNS list so the caller can persist it for later restore. If called twice, the
    /// SECOND call must NOT clobber the originally-saved DNS — it returns the originals captured
    /// the first time.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> OverrideToLoopback();

    /// <summary>Restore each adapter's DNS to the supplied originals. Adapters absent now are skipped.</summary>
    void Restore(IReadOnlyDictionary<string, IReadOnlyList<string>> originals);
}
