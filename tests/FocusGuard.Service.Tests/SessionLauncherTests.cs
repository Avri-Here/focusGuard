using System.Runtime.InteropServices;
using FocusGuard.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusGuard.Service.Tests;

/// <summary>
/// Smoke tests for <see cref="SessionLauncher"/>. We never try to actually spawn a UI process
/// from the test runner — the goal is just to exercise the boundary functions and confirm they
/// fail cleanly (return null/false) instead of throwing.
/// </summary>
public class SessionLauncherTests
{
    [SkippableFact]
    public void HasInteractiveUser_does_not_throw()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var launcher = new SessionLauncher(NullLogger<SessionLauncher>.Instance);

        // Either true or false is acceptable; what we're verifying is that it returns
        // cleanly without throwing — even when called from a non-service context.
        _ = launcher.HasInteractiveUser();
    }

    [SkippableFact]
    public void Launch_returns_null_for_missing_executable_without_throwing()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var launcher = new SessionLauncher(NullLogger<SessionLauncher>.Instance);

        // Even if HasInteractiveUser returns true (running from interactive logon for the dev),
        // CreateProcessAsUser of a non-existent path will fail and we want a null return,
        // not an exception.
        var pid = launcher.Launch(@"C:\does-not-exist-" + Guid.NewGuid().ToString("N") + ".exe");

        Assert.Null(pid);
    }

    [SkippableFact]
    public void Launch_returns_null_when_called_with_empty_path()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var launcher = new SessionLauncher(NullLogger<SessionLauncher>.Instance);

        Assert.Null(launcher.Launch(string.Empty));
    }

    [Fact]
    public void NoOpSessionLauncher_returns_null_and_false()
    {
        var launcher = new NoOpSessionLauncher();
        Assert.False(launcher.HasInteractiveUser());
        Assert.Null(launcher.Launch(@"C:\anything.exe"));
    }
}
