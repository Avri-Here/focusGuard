using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FocusGuard.Core;
using Microsoft.Extensions.Logging;
using WindowsFirewallHelper;
using WindowsFirewallHelper.Addresses;
using WindowsFirewallHelper.FirewallRules;

namespace FocusGuard.Service.Network;

/// <summary>
/// Real <see cref="IFirewallManager"/> backed by the Windows Advanced Firewall (WAS) via the
/// <c>WindowsFirewallHelper</c> NuGet wrapper. Owns three static rules and a dynamic per-IP allow set:
/// <list type="bullet">
///   <item><c>FG-BlockAll</c> — block all outbound (TCP+UDP), all profiles. Toggled by <see cref="ApplyPosture"/>.</item>
///   <item><c>FG-AllowLoopback</c> — allow outbound to 127.0.0.0/8 so the local DNS resolver is reachable.</item>
///   <item><c>FG-AllowService</c> — allow outbound for the service exe so it can forward DNS upstream.</item>
///   <item><c>FG-AllowIP-&lt;hash&gt;</c> — one per whitelisted IP, refreshed by <see cref="UpsertAllowIp"/>.</item>
/// </list>
/// All operations are idempotent — re-running EnsureStaticRules after a service restart picks up
/// the existing rules instead of duplicating them. Requires running as an elevated/SYSTEM process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallManager : IFirewallManager
{
    public const string RulePrefix = "FG-";
    public const string BlockAllRuleName = "FG-BlockAll";
    public const string AllowLoopbackRuleName = "FG-AllowLoopback";
    public const string AllowServiceRuleName = "FG-AllowService";
    public const string AllowIpRulePrefix = "FG-AllowIP-";

    private const FirewallProfiles AllProfiles =
        FirewallProfiles.Domain | FirewallProfiles.Private | FirewallProfiles.Public;

    private readonly ILogger<FirewallManager> _logger;
    private readonly string _serviceExePath;

    public FirewallManager(ILogger<FirewallManager> logger)
        : this(logger, Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine service exe path"))
    {
    }

    internal FirewallManager(ILogger<FirewallManager> logger, string serviceExePath)
    {
        _logger = logger;
        _serviceExePath = serviceExePath;
    }

    public void EnsureStaticRules()
    {
        var firewall = FirewallWAS.Instance;

        EnsureBlockAll(firewall);
        EnsureAllowLoopback(firewall);
        EnsureAllowService(firewall);

        _logger.LogInformation("FirewallManager: static rules ensured");
    }

    public void ApplyPosture(NetworkPosture posture)
    {
        var firewall = FirewallWAS.Instance;
        var rule = FindByName(firewall, BlockAllRuleName)
            ?? throw new InvalidOperationException($"{BlockAllRuleName} not installed; call EnsureStaticRules first");

        // Closed posture = block-all enabled. Open posture = block-all disabled (other rules become moot).
        var shouldBeEnabled = posture == NetworkPosture.Closed;
        if (rule.IsEnable != shouldBeEnabled)
        {
            rule.IsEnable = shouldBeEnabled;
            _logger.LogInformation("FirewallManager: {Rule} -> {State} (posture={Posture})",
                BlockAllRuleName, shouldBeEnabled ? "enabled" : "disabled", posture);
        }
    }

    public void UpsertAllowIp(string ip, TimeSpan ttl)
    {
        if (!IPAddress.TryParse(ip, out var parsed))
            throw new ArgumentException($"Not a valid IP: {ip}", nameof(ip));

        var firewall = FirewallWAS.Instance;
        var name = AllowIpRuleName(ip);
        var existing = FindByName(firewall, name);
        if (existing is not null)
        {
            existing.RemoteAddresses = new IAddress[] { new SingleIP(parsed) };
            existing.IsEnable = true;
            _logger.LogDebug("FirewallManager: refreshed {Rule} (ttl={Ttl})", name, ttl);
            return;
        }

        var rule = new FirewallWASRuleWin8(
            name,
            FirewallAction.Allow,
            FirewallDirection.Outbound,
            AllProfiles)
        {
            Protocol = FirewallProtocol.Any,
            RemoteAddresses = new IAddress[] { new SingleIP(parsed) },
            Description = $"FocusGuard whitelist allow (ttl={ttl})",
            Grouping = "FocusGuard",
            IsEnable = true,
        };
        firewall.Rules.Add(rule);
        _logger.LogInformation("FirewallManager: added {Rule} for {Ip} (ttl={Ttl})", name, ip, ttl);
    }

    public void RemoveAllowIp(string ip)
    {
        var firewall = FirewallWAS.Instance;
        var name = AllowIpRuleName(ip);
        if (RemoveByName(firewall, name))
            _logger.LogInformation("FirewallManager: removed {Rule}", name);
    }

    private void EnsureBlockAll(FirewallWAS firewall)
    {
        var existing = FindByName(firewall, BlockAllRuleName);
        if (existing is not null) return;

        var rule = new FirewallWASRuleWin8(
            BlockAllRuleName,
            FirewallAction.Block,
            FirewallDirection.Outbound,
            AllProfiles)
        {
            Protocol = FirewallProtocol.Any,
            Description = "FocusGuard: block all outbound while in Blocked state",
            Grouping = "FocusGuard",
            IsEnable = true,
        };
        firewall.Rules.Add(rule);
        _logger.LogInformation("FirewallManager: created {Rule}", BlockAllRuleName);
    }

    private void EnsureAllowLoopback(FirewallWAS firewall)
    {
        var existing = FindByName(firewall, AllowLoopbackRuleName);
        if (existing is not null) return;

        var rule = new FirewallWASRuleWin8(
            AllowLoopbackRuleName,
            FirewallAction.Allow,
            FirewallDirection.Outbound,
            AllProfiles)
        {
            Protocol = FirewallProtocol.Any,
            RemoteAddresses = new IAddress[]
            {
                new NetworkAddress(IPAddress.Parse("127.0.0.0"), IPAddress.Parse("255.0.0.0")),
            },
            Description = "FocusGuard: allow outbound to local DNS resolver",
            Grouping = "FocusGuard",
            IsEnable = true,
        };
        firewall.Rules.Add(rule);
        _logger.LogInformation("FirewallManager: created {Rule}", AllowLoopbackRuleName);
    }

    private void EnsureAllowService(FirewallWAS firewall)
    {
        var existing = FindByName(firewall, AllowServiceRuleName);
        if (existing is not null)
        {
            // Path may have changed across upgrades; refresh.
            if (!string.Equals(existing.ApplicationName, _serviceExePath, StringComparison.OrdinalIgnoreCase))
            {
                existing.ApplicationName = _serviceExePath;
                _logger.LogInformation("FirewallManager: refreshed {Rule} path", AllowServiceRuleName);
            }
            return;
        }

        var rule = new FirewallWASRuleWin8(
            AllowServiceRuleName,
            _serviceExePath,
            FirewallAction.Allow,
            FirewallDirection.Outbound,
            AllProfiles)
        {
            Protocol = FirewallProtocol.Any,
            Description = "FocusGuard: allow service exe to forward DNS upstream",
            Grouping = "FocusGuard",
            IsEnable = true,
        };
        firewall.Rules.Add(rule);
        _logger.LogInformation("FirewallManager: created {Rule} for {Exe}", AllowServiceRuleName, _serviceExePath);
    }

    private static FirewallWASRule? FindByName(FirewallWAS firewall, string name)
    {
        foreach (var rule in firewall.Rules)
        {
            if (string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    private static bool RemoveByName(FirewallWAS firewall, string name)
    {
        var rule = FindByName(firewall, name);
        if (rule is null) return false;
        return firewall.Rules.Remove(rule);
    }

    public static string AllowIpRuleName(string ip)
    {
        // Names must be unique-per-IP, stable, and short. Hash + prefix avoids any IP-character
        // surprises (IPv6 colons, brackets) in the rule name.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(ip), hash);
        var hex = new StringBuilder(AllowIpRulePrefix.Length + 16);
        hex.Append(AllowIpRulePrefix);
        for (var i = 0; i < 8; i++)
            hex.Append(hash[i].ToString("x2"));
        return hex.ToString();
    }
}
