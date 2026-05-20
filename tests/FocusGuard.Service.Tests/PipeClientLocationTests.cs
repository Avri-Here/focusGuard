// Deliberately does NOT import FocusGuard.Service.Ipc — this asserts at compile-time that
// PipeClient and PipeFraming live in FocusGuard.Core.Ipc so the Tray (which depends only on
// Core) can use them without referencing the Service assembly.
using FocusGuard.Core.Ipc;

namespace FocusGuard.Service.Tests;

public class PipeClientLocationTests
{
    [Fact]
    public void PipeClient_type_is_in_FocusGuard_Core_Ipc_namespace()
    {
        var ns = typeof(PipeClient).Namespace;
        Assert.Equal("FocusGuard.Core.Ipc", ns);
    }

    [Fact]
    public void PipeFraming_type_is_in_FocusGuard_Core_Ipc_namespace()
    {
        var ns = typeof(PipeFraming).Namespace;
        Assert.Equal("FocusGuard.Core.Ipc", ns);
    }

    [Fact]
    public void PipeClient_assembly_is_FocusGuard_Core()
    {
        // The whole point of moving these is that the Tray app (which only references
        // Core) can use them. If this regresses, somebody re-introduced a Service-side
        // copy that shadows the Core one.
        var asm = typeof(PipeClient).Assembly.GetName().Name;
        Assert.Equal("FocusGuard.Core", asm);
    }
}
