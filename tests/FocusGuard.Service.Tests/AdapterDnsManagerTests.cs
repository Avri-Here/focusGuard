using FocusGuard.Service.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Tests for <see cref="AdapterDnsManager"/> using a fake adapter backend so we never touch the
/// real network adapters from the test process. The real WMI-backed implementation is exercised
/// only by an out-of-band manual smoke test on a VM (per CLAUDE.md guidance).
/// </summary>
public class AdapterDnsManagerTests
{
    [Fact]
    public void Override_pushes_loopback_to_each_adapter_and_records_originals()
    {
        var backend = new FakeAdapterBackend();
        backend.Adapters["{A}"] = new[] { "192.168.1.1", "8.8.8.8" };
        backend.Adapters["{B}"] = new[] { "10.0.0.1" };

        var mgr = new AdapterDnsManager(backend, NullLogger<AdapterDnsManager>.Instance);
        var saved = mgr.OverrideToLoopback();

        Assert.Equal(new[] { "127.0.0.1" }, backend.Adapters["{A}"]);
        Assert.Equal(new[] { "127.0.0.1" }, backend.Adapters["{B}"]);
        Assert.Equal(2, saved.Count);
        Assert.Equal(new[] { "192.168.1.1", "8.8.8.8" }, saved["{A}"]);
        Assert.Equal(new[] { "10.0.0.1" }, saved["{B}"]);
    }

    [Fact]
    public void Override_does_not_overwrite_originals_when_called_twice()
    {
        var backend = new FakeAdapterBackend();
        backend.Adapters["{A}"] = new[] { "192.168.1.1" };
        var mgr = new AdapterDnsManager(backend, NullLogger<AdapterDnsManager>.Instance);

        var first = mgr.OverrideToLoopback();
        var second = mgr.OverrideToLoopback();

        // Second override should still report the *original* DNS, not 127.0.0.1 (which is what the
        // backend now reports). Otherwise restoring after a service restart would set adapters to
        // 127.0.0.1.
        Assert.Equal(new[] { "192.168.1.1" }, second["{A}"]);
        Assert.Equal(first["{A}"], second["{A}"]);
    }

    [Fact]
    public void Restore_writes_saved_originals_back_to_each_adapter()
    {
        var backend = new FakeAdapterBackend();
        backend.Adapters["{A}"] = new[] { "127.0.0.1" }; // current = sinkhole already pushed

        var mgr = new AdapterDnsManager(backend, NullLogger<AdapterDnsManager>.Instance);
        var originals = new Dictionary<string, IReadOnlyList<string>>
        {
            ["{A}"] = new[] { "192.168.1.1", "8.8.8.8" },
        };

        mgr.Restore(originals);

        Assert.Equal(new[] { "192.168.1.1", "8.8.8.8" }, backend.Adapters["{A}"]);
    }

    [Fact]
    public void Restore_clears_adapter_dns_when_originals_are_empty()
    {
        var backend = new FakeAdapterBackend();
        backend.Adapters["{A}"] = new[] { "127.0.0.1" };

        var mgr = new AdapterDnsManager(backend, NullLogger<AdapterDnsManager>.Instance);
        var originals = new Dictionary<string, IReadOnlyList<string>>
        {
            ["{A}"] = Array.Empty<string>(),
        };

        mgr.Restore(originals);

        // Empty list = adapter was DHCP-assigned originally; restore should clear back to DHCP.
        Assert.Empty(backend.Adapters["{A}"]);
    }

    [Fact]
    public void Restore_skips_adapters_no_longer_present()
    {
        var backend = new FakeAdapterBackend();
        // Adapter "{A}" has been removed since override was called.
        var mgr = new AdapterDnsManager(backend, NullLogger<AdapterDnsManager>.Instance);
        var originals = new Dictionary<string, IReadOnlyList<string>>
        {
            ["{A}"] = new[] { "192.168.1.1" },
        };

        // Should not throw.
        mgr.Restore(originals);

        Assert.Empty(backend.Adapters);
    }
}

/// <summary>Test backend: in-memory adapter map. Treats DNS list as the entire adapter state.</summary>
internal sealed class FakeAdapterBackend : INetworkAdapterBackend
{
    public Dictionary<string, string[]> Adapters { get; } = new();

    public IReadOnlyList<NetworkAdapterInfo> EnumerateActive()
    {
        return Adapters
            .Select(kv => new NetworkAdapterInfo(kv.Key, kv.Value))
            .ToArray();
    }

    public void SetDnsServers(string adapterId, IReadOnlyList<string> dnsServers)
    {
        if (Adapters.ContainsKey(adapterId))
            Adapters[adapterId] = dnsServers.ToArray();
    }
}
