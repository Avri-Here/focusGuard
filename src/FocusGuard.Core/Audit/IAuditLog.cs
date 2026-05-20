namespace FocusGuard.Core.Audit;

public enum AuditCategory
{
    AdminAction,
    StateTransition,
    AuthFailure,
    Tamper,
}

/// <summary>
/// Plaintext rolling audit log. Production: <see cref="FileAuditLog"/>. Tests use an in-memory fake.
/// One line per call; format is line-stable so an admin can read it with notepad / Get-Content.
/// </summary>
public interface IAuditLog
{
    void Append(AuditCategory category, string message);
}
