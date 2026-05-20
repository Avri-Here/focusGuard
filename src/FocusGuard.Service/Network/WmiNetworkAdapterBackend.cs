using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Network;

/// <summary>
/// Production backend for <see cref="AdapterDnsManager"/>. Talks to
/// <c>Win32_NetworkAdapterConfiguration</c> via System.Management. Only IP-enabled adapters are
/// touched, mirroring what <c>Set-DnsClientServerAddress</c> would do.
///
/// Live tests against this class must run elevated and would mutate the host's DNS
/// configuration — they are out of scope for the unit-test suite. The unit-testable surface is
/// covered through <see cref="INetworkAdapterBackend"/> with an in-memory fake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiNetworkAdapterBackend : INetworkAdapterBackend
{
    private readonly ILogger<WmiNetworkAdapterBackend> _logger;

    public WmiNetworkAdapterBackend(ILogger<WmiNetworkAdapterBackend> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<NetworkAdapterInfo> EnumerateActive()
    {
        var results = new List<NetworkAdapterInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT SettingID, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        foreach (var obj in searcher.Get())
        {
            using var mo = (ManagementObject)obj;
            var id = mo["SettingID"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var dns = (string[]?)mo["DNSServerSearchOrder"] ?? Array.Empty<string>();
            results.Add(new NetworkAdapterInfo(id, dns));
        }
        return results;
    }

    public void SetDnsServers(string adapterId, IReadOnlyList<string> dnsServers)
    {
        var query = "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE AND SettingID = '"
                    + adapterId.Replace("'", "''") + "'";
        using var searcher = new ManagementObjectSearcher(query);
        foreach (var obj in searcher.Get())
        {
            using var mo = (ManagementObject)obj;
            using var inParams = mo.GetMethodParameters("SetDNSServerSearchOrder");
            inParams["DNSServerSearchOrder"] = dnsServers.ToArray();
            using var outParams = mo.InvokeMethod("SetDNSServerSearchOrder", inParams, null);
            var rc = Convert.ToUInt32(outParams?["ReturnValue"] ?? 0u);
            if (rc != 0)
                _logger.LogWarning("WmiNetworkAdapterBackend: SetDNSServerSearchOrder on {Id} returned {Rc}", adapterId, rc);
            return;
        }
        _logger.LogInformation("WmiNetworkAdapterBackend: adapter {Id} not found / not IP-enabled", adapterId);
    }
}
