namespace InfiniContext.Core.Tasks.Skill;

/// <summary>
/// The host-installed user-level Claude Code skill that teaches the agent how to work with
/// InfiniContext tasks. <see cref="SkillInstaller"/> writes the contents of <see cref="SkillMarkdown"/>
/// to <see cref="GetSkillFilePath"/> on host startup.
///
/// <para>
/// Per the Phase 0 verification (research.md): all user-level skills are auto-discovered by
/// Claude Code and their <c>description</c> fields appear in the system prompt; the skill
/// <i>body</i> is loaded only when the model invokes the skill via the <c>Skill</c> tool. The
/// description below mentions <c>.task/</c> so the skill fires opportunistically when Claude
/// inspects a task-bearing cwd, but the host's first user message in a <c>/task switch</c>
/// session also explicitly names the skill (Mechanism 5B) for deterministic invocation.
/// </para>
///
/// <para>
/// <b>Versioning.</b> The skill file ends with an HTML comment marker:
/// <c>&lt;!-- infinicontext-task-skill-version: N --&gt;</c>. The installer parses this on each launch
/// and rewrites the file when <see cref="Version"/> in code is higher than the on-disk value.
/// Bump <see cref="Version"/> any time <see cref="SkillMarkdown"/> changes.
/// </para>
/// </summary>
public static class InfiniContextTaskSkill
{
    /// <summary>Skill name, also the folder under <c>~/.claude/skills/</c>.</summary>
    public const string SkillName = "infinicontext-task";

    /// <summary>Embedded version stamp. Increment on every change to <see cref="SkillMarkdown"/>.</summary>
    public const int Version = 2;

    /// <summary>Marker prefix inside the SKILL.md file. The trailing integer is the version.</summary>
    public const string VersionMarkerPrefix = "<!-- infinicontext-task-skill-version: ";

    /// <summary>Marker suffix.</summary>
    public const string VersionMarkerSuffix = " -->";

    /// <summary>
    /// Resolve the install directory under a given user-profile root. Pass <c>null</c> to use
    /// the system <see cref="Environment.SpecialFolder.UserProfile"/>; tests pass a temp dir.
    /// </summary>
    public static string GetInstallDir(string? userProfilePath = null)
    {
        userProfilePath ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfilePath, ".claude", "skills", SkillName);
    }

    public static string GetSkillFilePath(string? userProfilePath = null)
        => Path.Combine(GetInstallDir(userProfilePath), "SKILL.md");

    /// <summary>
    /// Full SKILL.md content (frontmatter + body + version marker). Total body kept under
    /// 500 tokens per design.md risk #5. v2 (Phase 7) adds the MCP lock contract.
    /// </summary>
    public static string SkillMarkdown { get; } = $$"""
        ---
        name: {{SkillName}}
        description: Use when working with InfiniContext tasks — artifacts in `.task/<id>-<slug>/` folders. The host invokes this skill at the start of any `/task switch` session and names the active task id. Covers loading the task index, maintaining `progress.md`, reconciling drift, the per-file lock contract for parallel sessions, and complying with host context-pressure nudges.
        ---

        # InfiniContext task management

        You are working on a InfiniContext-tracked task. The host has scaffolded the task folder at `.task/NNNN-slug/` and named it in the first user message. The folder is the single source of truth for everything the host has stored about this task.

        ## On session start

        1. **Read `index.md` first.** It lists every artifact file with a one-line description and a "last updated" date. Use it to decide what else to read.
        2. **Read only what you need.** Don't preload `description.md` or `progress.md` if the user's prompt is about something else. Each load costs context budget.
        3. **If the host's first user message says files are missing from the index, reconcile:** add a row to `index.md` for each missing file (description = your best one-liner; date = today).

        ## Editing artifacts: the lock contract (parallel-session safety)

        Multiple sessions may be working on the same task at once. To keep edits coherent, **every write to a file under `.task/` must be wrapped in an acquire/release pair**:

        1. Call `mcp__infinicontext__task_acquire_lock` with `{ "file": "<full path you'll edit>" }`. Wait for `{ "token": "lt_…", "ttl_seconds": N }`.
        2. `Read` the file (Claude already requires a fresh read before `Edit`; this also ensures you see another session's just-released changes).
        3. Make your edit with `Edit` or `Write`.
        4. Call `mcp__infinicontext__task_release_lock` with `{ "token": "<the token you got>" }`.

        Rules:
        - **Always release**, even on the error path. If your edit fails, still call release before moving on.
        - **One file per acquire.** If you need to edit two files, do two acquire/release pairs in sequence.
        - **TTL is short** (default 30s). If your edit takes longer (rare), expect another session may have moved on; re-read after re-acquire.
        - **A timeout response** (`{ "error": "timeout waiting for lock" }`) means another session has held the file too long. Tell the user what you wanted to do, then stop and let them decide whether to retry.
        - Reads alone don't need a lock. Locks gate writes.

        ## Index hygiene

        When you create a new artifact file (e.g. `research.md`, `decisions.md`, `plan.md`), append a row to `index.md` with the file name, a one-line description, and today's date in `YYYY-MM-DD`. Update the date column when you make a meaningful change to a listed file. (`index.md` itself is also a `.task/` artifact — lock it like any other before editing.)

        ## Progress hygiene

        Append a brief entry to `progress.md` at every meaningful state change: phase completion, decision recorded, blocker hit, hand-off to a new session. Group entries under day headings (`## YYYY-MM-DD`). Progress is the audit log; future-you will thank present-you.

        ## Host context-pressure nudges

        The host monitors your context usage and may inject one of two turns:

        - **Soft (~60%):** finish the current sub-task, write progress, then exit cleanly so a fresh session can resume without losing state.
        - **Hard (~85%):** flush *now* — update `progress.md` with where you are and what's left, then exit. Do not start new work or ask clarifying questions.

        ## Mandatory vs optional artifacts

        - **Mandatory** (host-scaffolded on creation): `index.md`, `description.md`, `progress.md`.
        - **Optional**, created on demand: `plan.md`, `research.md`, `decisions.md`, `design.md`, anything else useful for the task.

        Files live flat at the task-folder root — there is no `artifacts/` subfolder by design.

        {{VersionMarkerPrefix}}{{Version}}{{VersionMarkerSuffix}}
        """;
}
