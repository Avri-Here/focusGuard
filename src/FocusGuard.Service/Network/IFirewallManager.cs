using FocusGuard.Core;

namespace FocusGuard.Service.Network;

/// <summary>
/// Abstraction over the Windows Firewall rules that drive the network posture.
/// Production implementation: <see cref="FirewallManager"/>.
/// Tests substitute a recording fake.
/// </summary>
public interface IFirewallManager
{
    /// <summary>
    /// Idempotently install the static rule set (block-all outbound, allow-loopback,
    /// allow-service). Should be called on service startup.
    /// </summary>
    void EnsureStaticRules();

    /// <summary>Switch the master block-all rule on (Closed) or off (Open).</summary>
    void ApplyPosture(NetworkPosture posture);

    /// <summary>
    /// Add or refresh a dynamic <c>FG-AllowIP-&lt;hash&gt;</c> rule for a whitelist-resolved IP.
    /// </summary>
    void UpsertAllowIp(string ip, TimeSpan ttl);

    /// <summary>Remove a previously installed dynamic allow rule.</summary>
    void RemoveAllowIp(string ip);
}
