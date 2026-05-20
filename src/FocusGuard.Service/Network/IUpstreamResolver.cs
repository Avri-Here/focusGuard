namespace FocusGuard.Service.Network;

public enum UpstreamRecordType
{
    A,
    AAAA,
}

/// <summary>One IP-bearing record from an upstream answer.</summary>
public sealed record UpstreamRecord(string Ip, TimeSpan Ttl);

/// <summary>Combined upstream answer: NXDOMAIN or one-or-more records.</summary>
public sealed record UpstreamAnswer(bool IsNxDomain, IReadOnlyList<UpstreamRecord> Records);

/// <summary>
/// Abstraction over the upstream DNS forwarder used by <see cref="DnsSinkhole"/>. Production
/// implementation wraps <c>DnsClient.NET</c> against the configured upstream (default 1.1.1.1);
/// tests substitute a deterministic fake.
/// </summary>
public interface IUpstreamResolver
{
    Task<UpstreamAnswer> ResolveAsync(string domain, UpstreamRecordType type, CancellationToken ct);
}
