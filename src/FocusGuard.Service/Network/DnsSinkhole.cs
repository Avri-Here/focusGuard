using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Versioning;
using ARSoft.Tools.Net;
using ARSoft.Tools.Net.Dns;
using FocusGuard.Core;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Network;

/// <summary>
/// Local DNS sinkhole on 127.0.0.1:53. Behaviour depends on the posture set by
/// <see cref="SetPosture"/>:
/// <list type="bullet">
///   <item><see cref="NetworkPosture.Closed"/> (BLOCKED) — whitelisted domain → forward upstream,
///       upsert an allow rule per resolved IP, return the answer. Non-whitelisted → NXDOMAIN.</item>
///   <item><see cref="NetworkPosture.Open"/> (BROWSING/PAUSED) — forward everything upstream
///       unfiltered. The firewall is in OPEN posture so allow-rules are moot.</item>
/// </list>
/// Whitelist matching is suffix-based: an entry <c>example.com</c> matches the literal domain
/// AND any subdomain (<c>api.example.com</c>). Lookalike suffixes (<c>notexample.com</c>) do not
/// match.
///
/// The hot logic — query handling and TTL eviction — is fully decoupled from the ARSoft
/// <c>DnsServer</c> socket so unit tests can drive it via <see cref="HandleQueryAsync"/> and
/// <see cref="SweepExpired"/> without binding to UDP/53.
/// </summary>
public sealed class DnsSinkhole : IDnsSinkhole, IDisposable
{
    private readonly IFirewallManager _firewall;
    private readonly IUpstreamResolver _upstream;
    private readonly IClock _clock;
    private readonly Func<IEnumerable<string>> _whitelistSupplier;
    private readonly ILogger<DnsSinkhole> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiry = new(StringComparer.OrdinalIgnoreCase);
    private NetworkPosture _posture = NetworkPosture.Closed;
    private DnsServer? _server;

    public DnsSinkhole(
        IFirewallManager firewall,
        IUpstreamResolver upstream,
        IClock clock,
        Func<IEnumerable<string>> whitelistSupplier,
        ILogger<DnsSinkhole> logger)
    {
        _firewall = firewall;
        _upstream = upstream;
        _clock = clock;
        _whitelistSupplier = whitelistSupplier;
        _logger = logger;
    }

    public void SetPosture(NetworkPosture posture)
    {
        if (_posture != posture)
        {
            _logger.LogInformation("DnsSinkhole: posture {From} -> {To}", _posture, posture);
            _posture = posture;
        }
    }

    /// <summary>
    /// Resolve one domain through the sinkhole policy. The DNS server's per-query handler delegates
    /// here, and unit tests call this directly.
    /// </summary>
    public async Task<SinkholeAnswer> HandleQueryAsync(string domain, UpstreamRecordType type, CancellationToken ct)
    {
        var normalized = NormalizeDomain(domain);

        if (_posture == NetworkPosture.Open || IsWhitelisted(normalized))
        {
            var answer = await _upstream.ResolveAsync(normalized, type, ct).ConfigureAwait(false);
            if (answer.IsNxDomain)
                return new SinkholeAnswer(IsNxDomain: true, Array.Empty<SinkholeRecord>());

            // Only Closed-posture whitelist hits create allow-rules; Open posture lets the firewall
            // pass everything so we'd be churning rules for nothing.
            if (_posture == NetworkPosture.Closed)
            {
                foreach (var record in answer.Records)
                {
                    _firewall.UpsertAllowIp(record.Ip, record.Ttl);
                    _expiry[record.Ip] = _clock.UtcNow + record.Ttl;
                }
            }

            var records = answer.Records.Select(r => new SinkholeRecord(r.Ip, r.Ttl)).ToArray();
            return new SinkholeAnswer(IsNxDomain: false, records);
        }

        _logger.LogDebug("DnsSinkhole: NXDOMAIN for {Domain}", normalized);
        return new SinkholeAnswer(IsNxDomain: true, Array.Empty<SinkholeRecord>());
    }

    public void SweepExpired()
    {
        var now = _clock.UtcNow;
        foreach (var kv in _expiry)
        {
            if (kv.Value <= now && _expiry.TryRemove(kv.Key, out _))
            {
                _firewall.RemoveAllowIp(kv.Key);
                _logger.LogDebug("DnsSinkhole: evicted {Ip}", kv.Key);
            }
        }
    }

    private bool IsWhitelisted(string domain)
    {
        foreach (var entry in _whitelistSupplier())
        {
            var e = NormalizeDomain(entry);
            if (string.IsNullOrEmpty(e)) continue;
            if (string.Equals(domain, e, StringComparison.OrdinalIgnoreCase)) return true;
            if (domain.Length > e.Length
                && domain.EndsWith(e, StringComparison.OrdinalIgnoreCase)
                && domain[domain.Length - e.Length - 1] == '.')
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeDomain(string domain)
    {
        var trimmed = domain.Trim().TrimEnd('.');
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Bind the DNS server to 127.0.0.1:53 (UDP+TCP). Real I/O — call from the Worker on Windows.
    /// </summary>
    public void Start()
    {
        if (_server is not null) return;
        var udp = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, 53));
        var tcp = new TcpServerTransport(new IPEndPoint(IPAddress.Loopback, 53));
        _server = new DnsServer(new IServerTransport[] { udp, tcp });
        _server.QueryReceived += OnQueryReceivedAsync;
        _server.Start();
        _logger.LogInformation("DnsSinkhole: listening on 127.0.0.1:53");
    }

    public void Stop()
    {
        if (_server is null) return;
        try { _server.Stop(); } catch { /* best effort */ }
        _server = null;
    }

    public void Dispose() => Stop();

    private async Task OnQueryReceivedAsync(object sender, QueryReceivedEventArgs e)
    {
        if (e.Query is not DnsMessage msg || msg.Questions.Count == 0)
            return;

        var question = msg.Questions[0];
        var response = msg.CreateResponseInstance();
        response.ReturnCode = ReturnCode.NoError;

        UpstreamRecordType? type = question.RecordType switch
        {
            RecordType.A => UpstreamRecordType.A,
            RecordType.Aaaa => UpstreamRecordType.AAAA,
            _ => null,
        };

        if (type is null)
        {
            response.ReturnCode = ReturnCode.Refused;
            e.Response = response;
            return;
        }

        try
        {
            var answer = await HandleQueryAsync(question.Name.ToString(), type.Value, CancellationToken.None).ConfigureAwait(false);
            if (answer.IsNxDomain)
            {
                response.ReturnCode = ReturnCode.NxDomain;
            }
            else
            {
                foreach (var rec in answer.Records)
                {
                    if (!IPAddress.TryParse(rec.Ip, out var ip)) continue;
                    var ttlSeconds = (int)Math.Max(1, rec.Ttl.TotalSeconds);
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        response.AnswerRecords.Add(new ARecord(question.Name, ttlSeconds, ip));
                    else
                        response.AnswerRecords.Add(new AaaaRecord(question.Name, ttlSeconds, ip));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DnsSinkhole: upstream resolution failed for {Name}", question.Name);
            response.ReturnCode = ReturnCode.ServerFailure;
        }

        e.Response = response;
    }
}

/// <summary>Result of a sinkhole policy decision exposed for unit tests.</summary>
public sealed record SinkholeAnswer(bool IsNxDomain, IReadOnlyList<SinkholeRecord> Records);

/// <summary>One IP-bearing record returned to the DNS client.</summary>
public sealed record SinkholeRecord(string Ip, TimeSpan Ttl);
