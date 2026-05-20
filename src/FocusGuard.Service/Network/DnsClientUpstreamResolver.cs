using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Network;

/// <summary>
/// Production <see cref="IUpstreamResolver"/> backed by DnsClient.NET. Talks to the configured
/// upstream resolver(s) (default 1.1.1.1) and returns A / AAAA records as <see cref="UpstreamRecord"/>s.
/// </summary>
public sealed class DnsClientUpstreamResolver : IUpstreamResolver
{
    private readonly LookupClient _client;
    private readonly ILogger<DnsClientUpstreamResolver> _logger;

    public DnsClientUpstreamResolver(IEnumerable<IPEndPoint> upstreamServers, ILogger<DnsClientUpstreamResolver> logger)
    {
        var opts = new LookupClientOptions(upstreamServers.ToArray())
        {
            UseCache = false,
            Retries = 1,
            Timeout = TimeSpan.FromSeconds(3),
        };
        _client = new LookupClient(opts);
        _logger = logger;
    }

    public async Task<UpstreamAnswer> ResolveAsync(string domain, UpstreamRecordType type, CancellationToken ct)
    {
        var queryType = type switch
        {
            UpstreamRecordType.A => QueryType.A,
            UpstreamRecordType.AAAA => QueryType.AAAA,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        try
        {
            var result = await _client.QueryAsync(domain, queryType, cancellationToken: ct).ConfigureAwait(false);
            if (result.HasError && result.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
                return new UpstreamAnswer(IsNxDomain: true, Array.Empty<UpstreamRecord>());

            var records = new List<UpstreamRecord>();
            foreach (var record in result.Answers)
            {
                switch (record)
                {
                    case ARecord a:
                        records.Add(new UpstreamRecord(a.Address.ToString(), TimeSpan.FromSeconds(Math.Max(1, a.InitialTimeToLive))));
                        break;
                    case AaaaRecord aaaa:
                        records.Add(new UpstreamRecord(aaaa.Address.ToString(), TimeSpan.FromSeconds(Math.Max(1, aaaa.InitialTimeToLive))));
                        break;
                }
            }

            if (records.Count == 0)
                return new UpstreamAnswer(IsNxDomain: true, Array.Empty<UpstreamRecord>());

            return new UpstreamAnswer(IsNxDomain: false, records);
        }
        catch (DnsResponseException ex)
        {
            _logger.LogWarning(ex, "DnsClientUpstreamResolver: query failed for {Domain}", domain);
            return new UpstreamAnswer(IsNxDomain: true, Array.Empty<UpstreamRecord>());
        }
    }
}
