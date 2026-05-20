using System.Globalization;

namespace FocusGuard.Core.Audit;

/// <summary>
/// Plaintext daily-rolling audit log. One file per local-date (audit-YYYY-MM-DD.log). Files
/// older than 30 days are deleted opportunistically on each Append. Each line is
/// <c>&lt;ISO local timestamp&gt;\t&lt;category&gt;\t&lt;message&gt;</c>.
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    private const int RetentionDays = 30;

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private DateOnly _lastPruneDate;

    public FileAuditLog(string directory, IClock clock)
    {
        _directory = directory;
        _clock = clock;
        Directory.CreateDirectory(_directory);
    }

    public void Append(AuditCategory category, string message)
    {
        var local = _clock.LocalNow;
        var date = DateOnly.FromDateTime(local.DateTime);
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-ddTHH:mm:ss}\t{1}\t{2}",
            local.DateTime,
            category,
            message ?? string.Empty);

        var path = Path.Combine(_directory, $"audit-{date:yyyy-MM-dd}.log");
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
            if (_lastPruneDate != date)
            {
                Prune(date);
                _lastPruneDate = date;
            }
        }
    }

    private void Prune(DateOnly today)
    {
        var cutoff = today.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // name is "audit-YYYY-MM-DD"
            if (name.Length != "audit-YYYY-MM-DD".Length) continue;
            if (!DateOnly.TryParseExact(name.AsSpan(6), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
                continue;
            if (fileDate < cutoff)
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }
}
