using FocusGuard.Core;

namespace FocusGuard.Service.Network;

/// <summary>
/// Abstraction over the Windows Firewall rules that drive the network posture.
/// Real implementation lands in build step 4 (FirewallManager.cs); for the step-3 skeleton
/// the service uses <see cref="NoOpFirewallManager"/>.
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
