using FocusGuard.Core;
using FocusGuard.Service.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Unit tests for <see cref="DnsSinkhole"/> exercise the query-handling and TTL-eviction logic
/// without binding to a real socket. The actual ARSoft DnsServer plumbing is exercised only by
/// elevated-Windows integration smoke tests (out of scope here).
/// </summary>
public class DnsSinkholeTests
{
    private static readonly TimeSpan UpstreamTtl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Closed_posture_whitelist_hit_forwards_upstream_and_upserts_each_answer_ip()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("example.com", UpstreamRecordType.A, UpstreamTtl, "203.0.113.10", "203.0.113.11");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);

        var answer = await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);

        Assert.False(answer.IsNxDomain);
        Assert.Equal(new[] { "203.0.113.10", "203.0.113.11" }, answer.Records.Select(r => r.Ip));
        Assert.Equal(2, fw.Upserts.Count);
        Assert.Contains(fw.Upserts, u => u.Ip == "203.0.113.10" && u.Ttl == UpstreamTtl);
        Assert.Contains(fw.Upserts, u => u.Ip == "203.0.113.11" && u.Ttl == UpstreamTtl);
    }

    [Fact]
    public async Task Closed_posture_non_whitelist_returns_NXDOMAIN_and_does_not_upsert()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);

        var answer = await sinkhole.HandleQueryAsync("evil.com", UpstreamRecordType.A, CancellationToken.None);

        Assert.True(answer.IsNxDomain);
        Assert.Empty(fw.Upserts);
        Assert.Equal(0, upstream.QueryCount);
    }

    [Fact]
    public async Task Closed_posture_subdomain_of_whitelist_entry_is_allowed()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("api.example.com", UpstreamRecordType.A, UpstreamTtl, "203.0.113.20");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);

        var answer = await sinkhole.HandleQueryAsync("api.example.com", UpstreamRecordType.A, CancellationToken.None);

        Assert.False(answer.IsNxDomain);
        Assert.Single(fw.Upserts);
        Assert.Equal("203.0.113.20", fw.Upserts[0].Ip);
    }

    [Fact]
    public async Task Closed_posture_similar_suffix_is_NOT_treated_as_whitelisted()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);

        // notexample.com ends with "example.com" textually but is a different domain — must NXDOMAIN.
        var a = await sinkhole.HandleQueryAsync("notexample.com", UpstreamRecordType.A, CancellationToken.None);
        // examplecom (no dot) clearly different.
        var b = await sinkhole.HandleQueryAsync("examplecom", UpstreamRecordType.A, CancellationToken.None);

        Assert.True(a.IsNxDomain);
        Assert.True(b.IsNxDomain);
        Assert.Empty(fw.Upserts);
    }

    [Fact]
    public async Task Open_posture_forwards_all_queries_and_does_not_upsert()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("anything.com", UpstreamRecordType.A, UpstreamTtl, "198.51.100.7");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => Array.Empty<string>(), NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Open);

        var answer = await sinkhole.HandleQueryAsync("anything.com", UpstreamRecordType.A, CancellationToken.None);

        Assert.False(answer.IsNxDomain);
        Assert.Single(answer.Records);
        Assert.Empty(fw.Upserts);
    }

    [Fact]
    public async Task SweepExpired_removes_entries_past_ttl_and_calls_firewall_RemoveAllowIp()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("example.com", UpstreamRecordType.A, TimeSpan.FromSeconds(30), "203.0.113.10");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);
        await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);
        Assert.Single(fw.Upserts);

        // Just past TTL.
        clock.Advance(TimeSpan.FromSeconds(31));
        sinkhole.SweepExpired();

        Assert.Single(fw.Removes);
        Assert.Equal("203.0.113.10", fw.Removes[0]);
    }

    [Fact]
    public async Task SweepExpired_keeps_entries_with_remaining_ttl()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("example.com", UpstreamRecordType.A, TimeSpan.FromSeconds(60), "203.0.113.10");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);
        await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(30));
        sinkhole.SweepExpired();

        Assert.Empty(fw.Removes);
    }

    [Fact]
    public async Task Re_resolving_same_domain_refreshes_TTL_in_firewall()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("example.com", UpstreamRecordType.A, TimeSpan.FromSeconds(60), "203.0.113.10");

        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => new[] { "example.com" }, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);
        await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(45));
        await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);

        // Original would have expired after another 15s without refresh; with refresh, sweep at +50s
        // (5s after the second resolve) leaves it in place because TTL 60 from the second resolve.
        clock.Advance(TimeSpan.FromSeconds(20)); // total 65s
        sinkhole.SweepExpired();

        Assert.Empty(fw.Removes);
        Assert.Equal(2, fw.Upserts.Count);
    }

    [Fact]
    public async Task Whitelist_changes_take_effect_on_next_query()
    {
        var fw = new RecordingFirewall();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var upstream = new FakeUpstream();
        upstream.Add("example.com", UpstreamRecordType.A, UpstreamTtl, "203.0.113.10");

        var whitelist = new List<string>();
        var sinkhole = new DnsSinkhole(fw, upstream, clock, () => whitelist, NullLogger<DnsSinkhole>.Instance);
        sinkhole.SetPosture(NetworkPosture.Closed);

        var first = await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);
        Assert.True(first.IsNxDomain);

        whitelist.Add("example.com");
        var second = await sinkhole.HandleQueryAsync("example.com", UpstreamRecordType.A, CancellationToken.None);
        Assert.False(second.IsNxDomain);
    }
}

/// <summary>Test fake for <see cref="IUpstreamResolver"/>.</summary>
internal sealed class FakeUpstream : IUpstreamResolver
{
    private readonly Dictionary<(string, UpstreamRecordType), UpstreamAnswer> _answers = new();
    public int QueryCount { get; private set; }

    public void Add(string domain, UpstreamRecordType type, TimeSpan ttl, params string[] ips)
    {
        var records = ips.Select(ip => new UpstreamRecord(ip, ttl)).ToArray();
        _answers[(domain.ToLowerInvariant(), type)] = new UpstreamAnswer(IsNxDomain: false, records);
    }

    public Task<UpstreamAnswer> ResolveAsync(string domain, UpstreamRecordType type, CancellationToken ct)
    {
        QueryCount++;
        if (_answers.TryGetValue((domain.ToLowerInvariant(), type), out var answer))
            return Task.FromResult(answer);
        return Task.FromResult(new UpstreamAnswer(IsNxDomain: true, Array.Empty<UpstreamRecord>()));
    }
}
