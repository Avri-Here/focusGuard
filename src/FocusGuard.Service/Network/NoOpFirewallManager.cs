using FocusGuard.Core;
using Microsoft.Extensions.Logging;

namespace FocusGuard.Service.Network;

/// <summary>
/// Logging-only stand-in used until the real <see cref="IFirewallManager"/> arrives in step 4.
/// Lets the rest of the service (state machine wiring, IPC, persistence) be exercised end-to-end
/// without touching the Windows Firewall.
/// </summary>
public sealed class NoOpFirewallManager(ILogger<NoOpFirewallManager> logger) : IFirewallManager
{
    public void EnsureStaticRules() =>
        logger.LogInformation("NoOpFirewall: EnsureStaticRules");

    public void ApplyPosture(NetworkPosture posture) =>
        logger.LogInformation("NoOpFirewall: ApplyPosture {Posture}", posture);

    public void UpsertAllowIp(string ip, TimeSpan ttl) =>
        logger.LogInformation("NoOpFirewall: UpsertAllowIp {Ip} ttl={Ttl}", ip, ttl);

    public void RemoveAllowIp(string ip) =>
        logger.LogInformation("NoOpFirewall: RemoveAllowIp {Ip}", ip);
}
