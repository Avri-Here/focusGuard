using System.Text.Json;
using System.Text.Json.Serialization;

namespace FocusGuard.Core.Ipc;

public enum FocusState
{
    Blocked,
    Browsing,
    Paused,
    Disabled,
}

public static class IpcCommands
{
    public const string GetStatus = "GetStatus";
    public const string StartBudget = "StartBudget";
    public const string StopBudget = "StopBudget";
    public const string SetPassword = "SetPassword";
    public const string AddWhitelist = "AddWhitelist";
    public const string RemoveWhitelist = "RemoveWhitelist";
    public const string AdminPause = "AdminPause";
    public const string AdminEndPause = "AdminEndPause";
    public const string Disable = "Disable";
    public const string Enable = "Enable";
}

/// <summary>
/// Wire envelope. Server reads <see cref="Command"/>, then deserialises <see cref="Payload"/>
/// to the matching request DTO below.
/// </summary>
public sealed record IpcRequestEnvelope(string Command, JsonElement Payload);

public sealed record IpcResponseEnvelope(bool Success, string? Error, JsonElement? Result);

// ---- Unauthenticated requests ----

public sealed record GetStatusRequest;

public sealed record StatusResponse(
    FocusState State,
    double MinutesRemaining,
    int SecondsThisSession,
    DateTimeOffset? PauseEndAt,
    IReadOnlyList<string> Whitelist,
    DateTimeOffset CycleResetAt,
    bool RequiresPasswordSetup);

public sealed record StartBudgetRequest;

public sealed record StopBudgetRequest;

// ---- Password-gated requests ----
// First-time setup uses OldPassword = "" (the service treats an unset password as accepting empty).

public sealed record SetPasswordRequest(string OldPassword, string NewPassword);

public sealed record AddWhitelistRequest(string Password, string Domain);

public sealed record RemoveWhitelistRequest(string Password, string Domain);

public sealed record AdminPauseRequest(string Password, int DurationMinutes);

public sealed record AdminEndPauseRequest(string Password);

public sealed record DisableRequest(string Password);

public sealed record EnableRequest(string Password);

/// <summary>Empty result body for commands that have no return data on success.</summary>
public sealed record OkResponse;

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
