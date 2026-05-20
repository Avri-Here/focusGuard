using System.Runtime.Versioning;
using System.Security.Principal;
using FocusGuard.Core;
using FocusGuard.Service.Network;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsFirewallHelper;
using WindowsFirewallHelper.FirewallRules;
using FirewallManager = FocusGuard.Service.Network.FirewallManager;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Live smoke tests for <see cref="FirewallManager"/>. These hit the real Windows Advanced Firewall,
/// so they're gated:
/// <list type="bullet">
///   <item>Skip on non-Windows.</item>
///   <item>Skip when not running elevated (firewall edits require admin).</item>
///   <item>Skip when the production rules already exist — never touch a real FocusGuard install.</item>
/// </list>
/// Each test cleans up the rules it created via try/finally even if assertions fail.
/// </summary>
public class FirewallManagerTests
{
    [SkippableFact]
    public void AllowIpRuleName_is_deterministic_and_per_ip_unique()
    {
        // Pure helper — no admin or Windows needed.
        Skip.IfNot(OperatingSystem.IsWindows(), "Helper is Windows-only by attribute");
#pragma warning disable CA1416
        var a1 = FirewallManager.AllowIpRuleName("1.2.3.4");
        var a2 = FirewallManager.AllowIpRuleName("1.2.3.4");
        var b = FirewallManager.AllowIpRuleName("1.2.3.5");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.StartsWith(FirewallManager.AllowIpRulePrefix, a1);
#pragma warning restore CA1416
    }

    [SkippableFact]
    public void EnsureStaticRules_creates_all_three_and_is_idempotent()
    {
        SkipUnlessLive();
#pragma warning disable CA1416
        using var scope = new FirewallTestScope();
        scope.Manager.EnsureStaticRules();

        Assert.NotNull(FindRule(FirewallManager.BlockAllRuleName));
        Assert.NotNull(FindRule(FirewallManager.AllowLoopbackRuleName));
        Assert.NotNull(FindRule(FirewallManager.AllowServiceRuleName));

        // Second call must not throw and must not duplicate.
        scope.Manager.EnsureStaticRules();
        Assert.Equal(1, CountRulesNamed(FirewallManager.BlockAllRuleName));
        Assert.Equal(1, CountRulesNamed(FirewallManager.AllowLoopbackRuleName));
        Assert.Equal(1, CountRulesNamed(FirewallManager.AllowServiceRuleName));
#pragma warning restore CA1416
    }

    [SkippableFact]
    public void ApplyPosture_toggles_BlockAll_enabled_state()
    {
        SkipUnlessLive();
#pragma warning disable CA1416
        using var scope = new FirewallTestScope();
        scope.Manager.EnsureStaticRules();

        scope.Manager.ApplyPosture(NetworkPosture.Open);
        Assert.False(FindRule(FirewallManager.BlockAllRuleName)!.IsEnable);

        scope.Manager.ApplyPosture(NetworkPosture.Closed);
        Assert.True(FindRule(FirewallManager.BlockAllRuleName)!.IsEnable);
#pragma warning restore CA1416
    }

    [SkippableFact]
    public void UpsertAllowIp_adds_then_refreshes_then_RemoveAllowIp_deletes()
    {
        SkipUnlessLive();
#pragma warning disable CA1416
        using var scope = new FirewallTestScope();
        const string ip = "203.0.113.42"; // TEST-NET-3, never a real address
        var ruleName = FirewallManager.AllowIpRuleName(ip);
        scope.TrackForCleanup(ruleName);

        scope.Manager.UpsertAllowIp(ip, TimeSpan.FromMinutes(5));
        var added = FindRule(ruleName);
        Assert.NotNull(added);
        Assert.True(added!.IsEnable);

        // Second upsert must not duplicate.
        scope.Manager.UpsertAllowIp(ip, TimeSpan.FromMinutes(10));
        Assert.Equal(1, CountRulesNamed(ruleName));

        scope.Manager.RemoveAllowIp(ip);
        Assert.Null(FindRule(ruleName));
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static void SkipUnlessLive()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Live firewall tests require Windows");
        Skip.IfNot(IsElevated(), "Live firewall tests require an elevated process");
        Skip.If(ProductionRulesAlreadyInstalled(),
            "Skipping to avoid clobbering a real FocusGuard install on this machine");
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private static bool ProductionRulesAlreadyInstalled()
    {
        return FindRule(FirewallManager.BlockAllRuleName) is not null
            || FindRule(FirewallManager.AllowLoopbackRuleName) is not null
            || FindRule(FirewallManager.AllowServiceRuleName) is not null;
    }

    [SupportedOSPlatform("windows")]
    private static FirewallWASRule? FindRule(string name)
    {
        foreach (var rule in FirewallWAS.Instance.Rules)
        {
            if (string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase))
                return (FirewallWASRule)rule;
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static int CountRulesNamed(string name)
    {
        var count = 0;
        foreach (var rule in FirewallWAS.Instance.Rules)
        {
            if (string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    /// <summary>
    /// RAII helper: builds a <see cref="FirewallManager"/> and removes everything it created
    /// (the three static rules plus any IP rules registered via <see cref="TrackForCleanup"/>)
    /// when disposed. Cleanup runs even if the test threw.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class FirewallTestScope : IDisposable
    {
        private readonly List<string> _toRemove = new()
        {
            FirewallManager.BlockAllRuleName,
            FirewallManager.AllowLoopbackRuleName,
            FirewallManager.AllowServiceRuleName,
        };

        public FirewallManager Manager { get; }

        public FirewallTestScope()
        {
            Manager = new FirewallManager(NullLogger<FirewallManager>.Instance);
        }

        public void TrackForCleanup(string ruleName) => _toRemove.Add(ruleName);

        public void Dispose()
        {
            var firewall = FirewallWAS.Instance;
            foreach (var name in _toRemove)
            {
                FirewallWASRule? rule;
                while ((rule = FindRule(name)) is not null)
                {
                    firewall.Rules.Remove(rule);
                }
            }
        }
    }
}

