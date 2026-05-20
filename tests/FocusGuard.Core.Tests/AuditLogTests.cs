using FocusGuard.Core.Audit;

namespace FocusGuard.Core.Tests;

public class AuditLogTests
{
    [Fact]
    public void Append_writes_one_line_per_call_to_the_current_day_file()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var log = new FileAuditLog(dir.Path, clock);

        log.Append(AuditCategory.AdminAction, "Disable: ok");
        log.Append(AuditCategory.AuthFailure, "Wrong password");

        var path = Path.Combine(dir.Path, "audit-2026-05-20.log");
        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("AdminAction", lines[0]);
        Assert.Contains("Disable: ok", lines[0]);
        Assert.Contains("AuthFailure", lines[1]);
    }

    [Fact]
    public void Append_rotates_to_new_file_at_local_midnight()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 23, 59, 59, TimeSpan.FromHours(3)));
        var log = new FileAuditLog(dir.Path, clock);

        log.Append(AuditCategory.StateTransition, "before midnight");
        clock.Advance(TimeSpan.FromSeconds(2));
        log.Append(AuditCategory.StateTransition, "after midnight");

        Assert.True(File.Exists(Path.Combine(dir.Path, "audit-2026-05-20.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "audit-2026-05-21.log")));
    }

    [Fact]
    public void Append_prunes_files_older_than_30_days()
    {
        using var dir = new TempDir();
        // Pre-seed a log file from 31 days ago and one from 29 days ago.
        File.WriteAllText(Path.Combine(dir.Path, "audit-2026-04-19.log"), "old\n");
        File.WriteAllText(Path.Combine(dir.Path, "audit-2026-04-21.log"), "kept\n");

        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        var log = new FileAuditLog(dir.Path, clock);
        log.Append(AuditCategory.AdminAction, "trigger prune");

        Assert.False(File.Exists(Path.Combine(dir.Path, "audit-2026-04-19.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "audit-2026-04-21.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "audit-2026-05-20.log")));
    }

    [Fact]
    public void Append_includes_local_time_and_category_prefix()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 20, 14, 30, 5, TimeSpan.FromHours(3)));
        var log = new FileAuditLog(dir.Path, clock);

        log.Append(AuditCategory.AdminAction, "AdminPause 15min");

        var lines = File.ReadAllLines(Path.Combine(dir.Path, "audit-2026-05-20.log"));
        Assert.Single(lines);
        // Format: "<ISO local timestamp>\t<category>\t<message>"
        Assert.StartsWith("2026-05-20T14:30:05", lines[0]);
        Assert.Contains("\tAdminAction\t", lines[0]);
        Assert.EndsWith("AdminPause 15min", lines[0]);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FocusGuardAudit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
