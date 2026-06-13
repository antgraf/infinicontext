using System.Text;
using System.Text.RegularExpressions;
using InfiniContext.Core.LightModel;

namespace InfiniContext.Core.Tasks;

/// <summary>
/// Orchestrates the host's task lifecycle: create → fork → merge → list → get. Combines
/// <see cref="TaskStore"/> (identity and lineage) with <see cref="TaskFolder"/> (filesystem) and
/// <see cref="ILightModelService"/> (title generation). REPL commands and session integration
/// in later phases call into this service rather than the lower-level types.
///
/// <para>
/// Slug recovery for existing tasks happens via <see cref="Slugify.For"/> on the stored title.
/// MVP doesn't support task rename, so the slug round-trip is deterministic. Once rename lands
/// post-MVP, a folder-discovery helper will need to scan <c>.task/</c> for the <c>NNNN-*</c>
/// prefix instead.
/// </para>
/// </summary>
public sealed class TaskService
{
    private readonly TaskStore _store;
    private readonly ILightModelService _lightModel;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Construct a service with explicit dependencies. <paramref name="clock"/> defaults to the
    /// system clock; pass a fake in tests.
    /// </summary>
    public TaskService(
        TaskStore store,
        ILightModelService lightModel,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _lightModel = lightModel ?? throw new ArgumentNullException(nameof(lightModel));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Create a new task. Calls the light model to derive a short title from
    /// <paramref name="description"/>, allocates an id, scaffolds the folder, writes the
    /// caller-supplied description text into <c>description.md</c>, and persists the row.
    /// <paramref name="defaultModel"/>, when non-null, is the model id sessions spawned for this
    /// task inherit unless their spawn request overrides it; null leaves the task on the host
    /// default (<c>settings.Session.Model</c> / <c>settings.Models.Main.Id</c>).
    /// </summary>
    public async Task<TaskRecord> CreateAsync(string homeCwd, string description, string? defaultModel = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeCwd);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        // Refuse home paths that look like build-output directories. Tasks scaffolded under
        // bin/Debug, bin/Release, or obj/ get wiped on rebuild and the registry row goes stale
        // (the 2026-05-09 manual-testing session was the surfacing event). Check before the
        // light-model call so a bad path doesn't cost the user a paid API turn.
        if (LooksLikeBuildOutput(homeCwd))
            throw new InvalidOperationException(
                $"Refusing to scaffold a task under \"{homeCwd}\": that path looks like a build-output " +
                "directory (contains bin/Debug, bin/Release, or obj/). Such folders are wiped on rebuild " +
                "and the registry row goes stale. Pass --home <stable-path> to /task new (typically the " +
                "repo root, not a build output).");

        // The light model runs with no filesystem tools (see CLAUDE.md "Light-model service"),
        // so a description that mentions local files or @-references can elicit a refusal like
        // "I don't have access to read files…" which then gets stored verbatim as the title.
        // Detect that shape and fall back to a deterministic title derived from the description;
        // the description itself is preserved into description.md regardless, so the user's
        // references survive for the /task switch session to act on.
        var rawTitle = await _lightModel.GenerateShortTitleAsync(description, maxWords: 6, ct).ConfigureAwait(false);
        var sanitized = SanitizeTitle(rawTitle);
        var title = LooksLikeRefusal(sanitized) ? DeriveFallbackTitle(description) : sanitized;
        var slug = Slugify.For(title);
        var id = await _store.AllocateIdAsync(ct).ConfigureAwait(false);

        var folder = new TaskFolder(homeCwd, id, slug);
        var today = DateOnly.FromDateTime(_clock().UtcDateTime);
        await folder.EnsureExistsAsync(today, ct).ConfigureAwait(false);
        await folder.WriteScaffoldedDescriptionAsync(BuildInitialDescription(title, description), ct).ConfigureAwait(false);

        // Best-effort: capture git HEAD so /task review has a reproducible diff base. Failure
        // (no git, not a repo, etc.) is tolerated -- review falls back to merge-base when null.
        var baseSha = await GitHead.TryResolveAsync(homeCwd, ct).ConfigureAwait(false);

        var nowMs = _clock().ToUnixTimeMilliseconds();
        var record = new TaskRecord(id, homeCwd, title, nowMs, nowMs, TaskItemStatus.InProgress, ParentTaskId: null, BaseCommitSha: baseSha, DefaultModel: NormalizeModel(defaultModel));
        await _store.CreateAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>Trim and null-out a blank model id so the column stores NULL rather than "".</summary>
    private static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    /// <summary>
    /// Fork an existing task: allocate a new id, create a new folder, copy all <c>.md</c> files
    /// from the source folder (preserving mtimes), insert the row, and record the lineage edge
    /// in <c>tasks.parent_task_id</c>. Title defaults to the source title with an incremented
    /// <c>(N)</c> suffix unless <paramref name="overrideTitle"/> is supplied.
    /// </summary>
    public async Task<TaskRecord> ForkAsync(int sourceId, string? overrideTitle = null, CancellationToken ct = default)
    {
        var source = await _store.GetAsync(sourceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Source task {sourceId} not found.");

        var title = SanitizeTitle(overrideTitle ?? AlgorithmicForkTitle(source.Title));
        var slug = Slugify.For(title);
        var childId = await _store.AllocateIdAsync(ct).ConfigureAwait(false);

        var sourceFolder = new TaskFolder(source.HomeCwd, source.Id, Slugify.For(source.Title));
        var childFolder = new TaskFolder(source.HomeCwd, childId, slug);

        var today = DateOnly.FromDateTime(_clock().UtcDateTime);
        await childFolder.EnsureExistsAsync(today, ct).ConfigureAwait(false);
        await sourceFolder.CopyArtifactsToAsync(childFolder, ct).ConfigureAwait(false);

        var nowMs = _clock().ToUnixTimeMilliseconds();
        // Idle, not InProgress (M14): no session has entered the new task yet. The
        // TaskActivityCoordinator flips it to InProgress when a session actually claims it; minting
        // it InProgress here left it wedged (delete refuses InProgress) when the fork was never
        // switched-to.
        var childRecord = new TaskRecord(childId, source.HomeCwd, title, nowMs, nowMs, TaskItemStatus.Idle, ParentTaskId: source.Id);
        await _store.CreateAsync(childRecord, ct).ConfigureAwait(false);
        await _store.RecordForkAsync(source.Id, childId, ct).ConfigureAwait(false);
        return childRecord;
    }

    /// <summary>
    /// Combine two or more existing tasks into a new task. MVP only implements
    /// <see cref="MergeTier.DeterministicConcat"/>. The merged folder gets, for every filename
    /// present in any source, a single file containing each source's content under labelled
    /// section dividers. The merged row's <c>parent_task_id</c> is left null (the schema
    /// records only a single parent; multi-parent lineage is post-MVP).
    /// </summary>
    public async Task<TaskRecord> MergeAsync(IReadOnlyList<int> sourceIds, MergeTier tier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        if (tier != MergeTier.DeterministicConcat)
            throw new NotImplementedException($"MergeTier.{tier} is post-MVP. Only DeterministicConcat is supported.");
        if (sourceIds.Count < 2)
            throw new ArgumentException("Merge requires at least two source tasks.", nameof(sourceIds));

        var sources = new List<(TaskRecord Record, TaskFolder Folder)>(sourceIds.Count);
        foreach (var id in sourceIds)
        {
            var rec = await _store.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Source task {id} not found.");
            sources.Add((rec, new TaskFolder(rec.HomeCwd, rec.Id, Slugify.For(rec.Title))));
        }

        var homeCwd = sources[0].Record.HomeCwd;
        if (sources.Any(s => !string.Equals(s.Record.HomeCwd, homeCwd, StringComparison.Ordinal)))
            throw new InvalidOperationException("All merge sources must share the same home_cwd. Cross-home merge is post-MVP.");

        var mergedTitle = "Merge of " + string.Join(" + ", sources.Select(s => s.Record.Id.ToString("D4")));
        var mergedSlug = Slugify.For(mergedTitle);
        var mergedId = await _store.AllocateIdAsync(ct).ConfigureAwait(false);
        var mergedFolder = new TaskFolder(homeCwd, mergedId, mergedSlug);
        var today = DateOnly.FromDateTime(_clock().UtcDateTime);
        await mergedFolder.EnsureExistsAsync(today, ct).ConfigureAwait(false);

        // Collect every filename present in any source. The set comparer is platform-gated
        // (Windows NTFS folds case; macOS/Linux case-sensitive volumes don't) so that on a
        // case-sensitive FS, Foo.md and foo.md are merged as distinct artifacts.
        var allNames = new SortedSet<string>(FileSystemNaming.Comparer);
        foreach (var (_, folder) in sources)
            foreach (var info in await folder.ListArtifactsAsync(ct).ConfigureAwait(false))
                allNames.Add(info.Name);

        foreach (var fileName in allNames)
        {
            // Skip index.md — it gets rewritten below to reflect the merged file list.
            if (string.Equals(fileName, ArtifactTemplates.IndexFileName, FileSystemNaming.Comparison)) continue;

            var sb = new StringBuilder();
            sb.Append("# ").Append(fileName).Append(" (merged from ").Append(sources.Count).AppendLine(" source tasks)").AppendLine();

            foreach (var (rec, folder) in sources)
            {
                sb.AppendLine("---").AppendLine();
                sb.Append("## From task ").Append(rec.Id.ToString("D4")).Append(" — ").AppendLine(rec.Title).AppendLine();

                var sourcePath = System.IO.Path.Combine(folder.Path, fileName);
                if (File.Exists(sourcePath))
                    sb.AppendLine(await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false));
                else
                    sb.AppendLine("_(no content for this file in this source)_");

                sb.AppendLine();
            }

            // arch-001: write merged artifacts crash-safely, consistent with the M18 AtomicFile
            // contract that IssueService / TaskCommandHandler already follow (these produce
            // index.md/progress.md-shaped task artifacts).
            await AtomicFile.WriteAllTextAsync(System.IO.Path.Combine(mergedFolder.Path, fileName), sb.ToString(), ct).ConfigureAwait(false);
        }

        // Rewrite the merged index.md with all artifacts that now exist on disk.
        await WriteMergedIndexAsync(mergedFolder, allNames, today, ct).ConfigureAwait(false);

        var nowMs = _clock().ToUnixTimeMilliseconds();
        // Idle, not InProgress (M14): see ForkAsync — the coordinator owns the InProgress flip when
        // a session enters the merged task.
        var record = new TaskRecord(mergedId, homeCwd, mergedTitle, nowMs, nowMs, TaskItemStatus.Idle, ParentTaskId: null);
        await _store.CreateAsync(record, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Delete a task row from the registry. Refuses when the row is <see cref="TaskItemStatus.InProgress"/>
    /// (active session would be pulled out from under the user). When <paramref name="removeFolder"/>
    /// is <c>true</c>, also recursively deletes the on-disk <c>.task/&lt;id&gt;-&lt;slug&gt;/</c> folder
    /// if it exists; otherwise the folder is left in place (so committed task content survives a
    /// registry cleanup). Children whose <c>parent_task_id</c> pointed at this row are orphaned
    /// (FK nulled) rather than cascade-deleted — multi-step lineage is informational, not
    /// load-bearing.
    /// </summary>
    /// <returns><c>null</c> if no task with that id exists; otherwise a <see cref="TaskDeleteResult"/>.</returns>
    public async Task<TaskDeleteResult?> DeleteAsync(int id, bool removeFolder, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (record is null) return null;

        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        var folderExisted = Directory.Exists(folder.Path);

        // M16: delete the DB row FIRST, with the InProgress guard re-verified atomically inside the
        // transaction. The prior code deleted the folder before the row and used a check-then-act
        // status guard — a concurrent EnterAsync could flip the row to InProgress after the check,
        // and a failure between folder-delete and row-delete left the user's content gone with a
        // surviving "stale row". Removing the row first means a later folder-delete failure leaves
        // recoverable content (folder present, row gone), never the reverse.
        var outcome = await _store.DeleteIfNotInProgressAsync(id, ct).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case DeleteOutcomeKind.NotFound:
                return null; // raced with another deleter
            case DeleteOutcomeKind.RefusedInProgress:
                throw new InvalidOperationException(
                    $"Task {id:D4} is InProgress; mark it Idle (or end its session) before deleting.");
        }

        var folderRemoved = false;
        if (removeFolder && folderExisted)
        {
            // Row is already gone; a failure here only orphans a folder (recoverable), so let it
            // surface to the caller rather than trying to resurrect the row.
            Directory.Delete(folder.Path, recursive: true);
            folderRemoved = true;
        }

        return new TaskDeleteResult(record, folder.Path, folderExisted, folderRemoved, outcome.OrphanedChildren);
    }

    public async Task<IReadOnlyList<TaskRecord>> ListAsync(ListFilter? filter = null, CancellationToken ct = default)
    {
        var all = await _store.ListAsync(ct).ConfigureAwait(false);
        IEnumerable<TaskRecord> q = all;
        if (filter?.Status is { } status) q = q.Where(r => r.Status == status);
        if (filter?.Limit is { } limit) q = q.Take(limit);
        return q.ToList();
    }

    public async Task<TaskDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (record is null) return null;
        var folder = new TaskFolder(record.HomeCwd, record.Id, Slugify.For(record.Title));
        var artifacts = await folder.ListArtifactsAsync(ct).ConfigureAwait(false);
        return new TaskDetails(record, artifacts);
    }

    /// <summary>
    /// Build a <see cref="TaskSwitchPlan"/> for <paramref name="id"/>: load the row, recover
    /// the slug, detect index/disk drift, and compose the first-user-message pointer. Returns
    /// <c>null</c> when the task does not exist. Callers (the REPL) hand the plan to the outer
    /// loop, which spawns a fresh <c>ClaudeSession</c> with
    /// <c>ClaudeSessionOptions.FirstUserMessage</c> set to <see cref="TaskSwitchPlan.FirstUserMessage"/>.
    /// </summary>
    public async Task<TaskSwitchPlan?> PrepareSwitchAsync(int id, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (record is null) return null;

        var slug = Slugify.For(record.Title);
        var folder = new TaskFolder(record.HomeCwd, record.Id, slug);

        // Stale-data guard surfaced by the 2026-05-09 manual-testing session: a task whose
        // home_cwd pointed at a build-output dir got wiped on rebuild, so /task switch landed
        // in a missing cwd and the first user message referenced a nonexistent folder. Fail
        // fast with a specific message — /task delete drops the dangling row.
        if (!Directory.Exists(record.HomeCwd))
            throw new InvalidOperationException(
                $"Task {id:D4} home_cwd \"{record.HomeCwd}\" no longer exists on disk — was it wiped by a build? " +
                $"Run /task delete {id:D4} to drop the stale row, or restore the folder.");
        if (!Directory.Exists(folder.Path))
            throw new InvalidOperationException(
                $"Task {id:D4} was scaffolded under \"{folder.Path}\" but that folder no longer exists. " +
                $"Run /task delete {id:D4} to drop the stale row, or restore the folder.");

        var driftReport = await folder.DetectDriftReportAsync(ct).ConfigureAwait(false);
        var firstMessage = TaskSessionPointer.ComposeFirstMessage(
            record.Id, slug, record.Title, driftReport.UnindexedFiles, driftReport.MissingFiles);
        return new TaskSwitchPlan(record, slug, firstMessage, driftReport.UnindexedFiles, driftReport.MissingFiles);
    }

    /// <summary>
    /// True when the path contains a <c>bin/Debug</c>, <c>bin/Release</c>, or <c>obj/</c>
    /// segment at any depth — heuristic for "this is a build output, not a stable place to
    /// keep durable task content". Matches on segment boundaries (so <c>objections/</c> is not
    /// a false positive). Public for tests; not part of the service's behavioural contract.
    /// </summary>
    public static bool LooksLikeBuildOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { full = path; }
        var segments = full.Split(
            new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // Path-segment comparison uses the same FS-naming gate as artifact identity: NTFS folds
        // `OBJ\` and `obj\` to the same dir; case-sensitive volumes do not. The convention is
        // lowercase `obj`/`bin` and `Debug`/`Release` (the .NET defaults), so a user who really
        // names their task home `~/projects/OBJ-storage/` on a case-sensitive FS won't trip the
        // guard.
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Equals("obj", FileSystemNaming.Comparison))
                return true;
            if (i + 1 < segments.Length
                && segments[i].Equals("bin", FileSystemNaming.Comparison)
                && (segments[i + 1].Equals("Debug", FileSystemNaming.Comparison)
                    || segments[i + 1].Equals("Release", FileSystemNaming.Comparison)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="title"/> looks like a refusal the light model emitted instead
    /// of a label — typically because the user's description referenced files the light-model
    /// back end can't read. Match is case-insensitive on a small set of canonical refusal
    /// openers. Public for tests; not part of the service's behavioural contract.
    /// </summary>
    public static bool LooksLikeRefusal(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var s = title.TrimStart().ToLowerInvariant();
        return s.StartsWith("i don't have access")
            || s.StartsWith("i do not have access")
            || s.StartsWith("i cannot ")
            || s.StartsWith("i can't ")
            || s.StartsWith("i'm unable")
            || s.StartsWith("i am unable")
            || s.StartsWith("i'm sorry")
            || s.StartsWith("i am sorry")
            || s.StartsWith("sorry, i ")
            || s.StartsWith("unfortunately,");
    }

    /// <summary>
    /// Build a deterministic fallback title from the user's description: take the first
    /// non-empty line and clamp to six whitespace-delimited words. Used when the light model
    /// refuses or returns an unusable title. The splitter intentionally does NOT break on
    /// <c>.</c> / <c>!</c> / <c>?</c> — those collide with file extensions (<c>docs/foo.md</c>)
    /// and decimal numbers; the six-word cap is the length budget. Public for tests; not part
    /// of the service's behavioural contract.
    /// </summary>
    public static string DeriveFallbackTitle(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Untitled task";
        var firstLine = description.Split(new[] { '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine)) return "Untitled task";
        var words = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Untitled task";
        return SanitizeTitle(string.Join(' ', words.Take(6)));
    }

    private static string BuildInitialDescription(string title, string body) => $"""
        # {title}

        {body}
        """;

    private static readonly Regex ForkSuffix = new(@"^(.*?)\s*\((\d+)\)\s*$", RegexOptions.Compiled);

    private static string AlgorithmicForkTitle(string sourceTitle)
    {
        var m = ForkSuffix.Match(sourceTitle);
        if (m.Success && int.TryParse(m.Groups[2].Value, out var n))
            return $"{m.Groups[1].Value} ({n + 1})";
        return $"{sourceTitle} (2)";
    }

    /// <summary>
    /// Trim quotes/whitespace and clamp length so the light model returning
    /// <c>"\"Implement Phase 3\"\n"</c> doesn't end up as the literal title.
    /// </summary>
    private static string SanitizeTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Untitled task";
        var trimmed = raw.Trim().Trim('"', '\u2018', '\u2019', '\u201C', '\u201D').Trim();
        if (trimmed.Length == 0) return "Untitled task";
        const int maxLen = 120;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen].TrimEnd();
    }

    private static async Task WriteMergedIndexAsync(TaskFolder folder, IEnumerable<string> fileNames, DateOnly today, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Task index").AppendLine();
        sb.AppendLine("| File | Description | Last updated |");
        sb.AppendLine("|------|-------------|--------------|");
        foreach (var name in fileNames.Where(n => !string.Equals(n, ArtifactTemplates.IndexFileName, FileSystemNaming.Comparison)))
        {
            sb.Append("| ").Append(name).Append(" | merged | ").Append(today.ToString("yyyy-MM-dd")).AppendLine(" |");
        }
        await AtomicFile.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, ArtifactTemplates.IndexFileName), sb.ToString(), ct).ConfigureAwait(false);
    }
}
