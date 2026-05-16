namespace InfiniContext.Core.Sessions;

/// <summary>
/// Inputs the host hands to <see cref="SessionManager.CreateAsync"/>. Mirrors the fields the
/// outer loop previously assembled into a <see cref="ClaudeSessionOptions"/>, with the addition
/// of <see cref="ActiveTaskId"/> so the resulting <see cref="SessionContext"/> remembers which
/// task it was opened against (the host has already negotiated the claim with
/// <c>TaskActivityCoordinator</c> before constructing this).
/// </summary>
public sealed record SpawnRequest
{
    public required string Cwd { get; init; }
    public string? Model { get; init; }
    public string? ResumeSessionId { get; init; }
    public string? FirstUserMessage { get; init; }
    public bool IncludePartialMessages { get; init; }
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
    public string? ClaudeExecutable { get; init; }
    public int? ActiveTaskId { get; init; }
}
