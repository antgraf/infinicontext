namespace InfiniContext.Core.Sessions;

/// <summary>
/// One row in <see cref="SessionIndex"/>. Mirrors the schema in <c>decisions.md</c> §D5: a record
/// of a session that the host opened, persisted under <c>~/.infinicontext/index.json</c> so that
/// after the host restarts the multi-session UI (Stage 2B Phase 4+) can offer recent sessions
/// as resume candidates.
/// </summary>
public sealed record SessionIndexEntry
{
    /// <summary>The session id assigned by <c>claude</c> (the value passed to <c>--resume</c>),
    /// not the host-side <c>SessionContext.Id</c>.</summary>
    public required string SessionId { get; init; }

    /// <summary>Working directory the session was spawned in.</summary>
    public required string Cwd { get; init; }

    /// <summary>Model alias the session ran under, or null when the host did not pin one
    /// (claude picked its default).</summary>
    public string? Model { get; init; }

    /// <summary>When <see cref="SessionManager.CreateAsync"/> registered the session.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last time the row was updated. Phase 3 only writes at close time, so this
    /// equals the close timestamp. Phase 4 may add throttled mid-run updates (D22).</summary>
    public required DateTimeOffset LastActive { get; init; }

    /// <summary>Optional user-set label. Reserved for Stage 3 UX (D5); Phase 3 always writes
    /// null here.</summary>
    public string? Title { get; init; }

    /// <summary>Session state machine state at close. Typically <c>Exited</c> for a clean exit
    /// but may capture intermediate states if the host process is force-killed mid-stream.</summary>
    public string? LastState { get; init; }

    /// <summary>Process exit code if known. Null when the host did not observe a clean process
    /// exit (race during shutdown, etc.).</summary>
    public int? ExitCode { get; init; }
}
