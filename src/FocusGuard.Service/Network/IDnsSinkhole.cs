using FocusGuard.Core;

namespace FocusGuard.Service.Network;

/// <summary>
/// Service-host-facing interface for the DNS sinkhole. The Worker drives posture changes through
/// it and runs <see cref="SweepExpired"/> on a periodic timer to evict allow-rules whose DNS TTL
/// has passed. Implementation: <see cref="DnsSinkhole"/>.
/// </summary>
public interface IDnsSinkhole
{
    /// <summary>Update the in-memory posture used to decide whether to NXDOMAIN non-whitelisted queries.</summary>
    void SetPosture(NetworkPosture posture);

    /// <summary>
    /// Walk the cache and call <see cref="IFirewallManager.RemoveAllowIp"/> for every entry whose
    /// TTL has passed against the host clock. Idempotent.
    /// </summary>
    void SweepExpired();

    /// <summary>
    /// Bind the resolver to its endpoint (in production: 127.0.0.1:53). Test fakes flip a flag.
    /// Idempotent.
    /// </summary>
    void Start();

    /// <summary>Release the bound endpoint. Idempotent.</summary>
    void Stop();
}
