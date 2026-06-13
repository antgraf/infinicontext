using Serilog;

namespace InfiniContext.Core.Review;

/// <summary>
/// Default <see cref="IAutoApplyExecutor"/>. Applies each <see cref="ConsolidatedFinding"/>'s
/// <see cref="SuggestedFix"/> to disk under the repo root, then runs the build + test gate via
/// <see cref="IBuildTestRunner"/>. If either gate fails, every edit applied in this batch is
/// reverted and every finding is demoted to <see cref="Disposition.NeedsUser"/>.
///
/// <para>
/// Per-finding outcomes (before the batch gate):
/// </para>
/// <list type="bullet">
/// <item><description><b>Applied:</b> file was readable, <c>OldText</c> matched exactly once (Edit) or kind was ReplaceFile, write succeeded.</description></item>
/// <item><description><b>Demoted with "source diverged":</b> for Edit, OldText didn't match the current file content (or matched multiple times, which is ambiguous).</description></item>
/// <item><description><b>Demoted with "target file outside repo root":</b> path traversal attempt.</description></item>
/// <item><description><b>Demoted with "target file does not exist":</b> Edit kind requires an existing file.</description></item>
/// <item><description><b>Demoted with "fix not auto-applicable":</b> kind=Manual or missing required text.</description></item>
/// </list>
/// </summary>
public sealed class AutoApplyExecutor : IAutoApplyExecutor
{
    private readonly IBuildTestRunner _gate;
    private readonly AutoApplyOptions _options;
    private readonly ILogger _logger;

    public AutoApplyExecutor(IBuildTestRunner gate, AutoApplyOptions? options = null, ILogger? logger = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options ?? new AutoApplyOptions();
        if (_options.ConfidenceFloor < 0 || _options.ConfidenceFloor > 1)
            throw new ArgumentException("AutoApplyOptions.ConfidenceFloor must be in [0,1].", nameof(options));
        if (_options.MaxLinesPerFinding <= 0)
            throw new ArgumentException("AutoApplyOptions.MaxLinesPerFinding must be positive.", nameof(options));
        if (_options.MaxFilesPerFinding <= 0)
            throw new ArgumentException("AutoApplyOptions.MaxFilesPerFinding must be positive.", nameof(options));
        _logger = logger ?? Log.ForContext<AutoApplyExecutor>();
    }

    public async Task<AutoApplyResult> ApplyAsync(
        string repoRoot,
        IReadOnlyList<ConsolidatedFinding> findings,
        CancellationToken ct,
        IReadOnlyCollection<string>? allowedRelativePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(findings);
        if (!Directory.Exists(repoRoot)) throw new DirectoryNotFoundException($"Repo root does not exist: {repoRoot}");

        var normalizedRoot = Path.GetFullPath(repoRoot);

        // C4 scope constraint set. Null = no constraint (legacy callers / tests that don't pass a
        // scope); the .git/CI/build-file denylist below always applies regardless. The scope paths
        // arrive already forward-slash-normalized (ReviewFile.RelativePath); key the set with
        // FileSystemNaming.Comparer so the membership test honors the platform's case rule.
        var allowedScope = allowedRelativePaths is null
            ? null
            : new HashSet<string>(allowedRelativePaths.Select(NormalizeRelative), FileSystemNaming.Comparer);

        // Baseline gate. If the build or test is already red BEFORE we apply anything, the
        // post-apply gate would either blame our edits for pre-existing failures (and revert
        // good work) or cover them up. Refuse the batch with a clear message instead. Only pay
        // the baseline cost when there's actually something to auto-apply.
        if (findings.Count > 0 && !_options.SkipBuildTestGate)
        {
            var baselineBuild = await _gate.BuildAsync(normalizedRoot, ct).ConfigureAwait(false);
            if (!baselineBuild.Passed)
            {
                var reason = $"baseline build was red before auto-apply: {baselineBuild.FailureSummary ?? "no detail"}. Fix the baseline build before re-running review with auto-apply.";
                _logger.Warning("auto-apply: {Reason}", reason);
                return new AutoApplyResult(
                    Applied: Array.Empty<ConsolidatedFinding>(),
                    Demoted: findings.Select(f => WithDemotion(f, reason)).ToList(),
                    BuildTestGatePassed: false,
                    GateReversionReason: reason);
            }
            var baselineTest = await _gate.TestAsync(normalizedRoot, ct).ConfigureAwait(false);
            if (!baselineTest.Passed)
            {
                var reason = $"baseline tests were red before auto-apply: {baselineTest.FailureSummary ?? "no detail"}. Fix the baseline tests before re-running review with auto-apply.";
                _logger.Warning("auto-apply: {Reason}", reason);
                return new AutoApplyResult(
                    Applied: Array.Empty<ConsolidatedFinding>(),
                    Demoted: findings.Select(f => WithDemotion(f, reason)).ToList(),
                    BuildTestGatePassed: false,
                    GateReversionReason: reason);
            }
        }

        // Per-file backups so we can revert if the gate fails. Key is the absolute path on disk;
        // value is the original bytes (or null when the file didn't exist before the apply -- in
        // which case revert = delete). Key with FileSystemNaming.Comparer, not OrdinalIgnoreCase:
        // on a case-sensitive filesystem Foo.cs and foo.cs are distinct files and must not share a
        // backup slot, or a gate-failure revert silently corrupts the working tree (M27).
        var backups = new Dictionary<string, byte[]?>(FileSystemNaming.Comparer);
        var applied = new List<ConsolidatedFinding>();
        var demoted = new List<ConsolidatedFinding>();

        foreach (var finding in findings)
        {
            ct.ThrowIfCancellationRequested();

            var demotion = await TryApplySingleAsync(finding, normalizedRoot, backups, allowedScope, ct).ConfigureAwait(false);
            if (demotion is null)
            {
                applied.Add(finding);
            }
            else
            {
                demoted.Add(WithDemotion(finding, demotion));
            }
        }

        if (applied.Count == 0)
        {
            // Nothing to gate. Return what we have.
            return new AutoApplyResult(applied, demoted, BuildTestGatePassed: true, GateReversionReason: null);
        }

        if (_options.SkipBuildTestGate)
        {
            // Skip path: applied as-is, no post-apply gate. Production should never use this.
            return new AutoApplyResult(applied, demoted, BuildTestGatePassed: true, GateReversionReason: null);
        }

        // Build gate.
        var buildOutcome = await _gate.BuildAsync(normalizedRoot, ct).ConfigureAwait(false);
        if (!buildOutcome.Passed)
        {
            var revertFailed = await RevertAsync(backups).ConfigureAwait(false);
            var reason = AppendRevertFailures(
                $"build broke after auto-apply: {buildOutcome.FailureSummary ?? "no detail"}",
                revertFailed);
            _logger.Warning("auto-apply: {Reason}; reverted {Count} files ({Failed} failed)", reason, backups.Count, revertFailed);
            return new AutoApplyResult(
                Applied: Array.Empty<ConsolidatedFinding>(),
                Demoted: applied.Select(f => WithDemotion(f, reason)).Concat(demoted).ToList(),
                BuildTestGatePassed: false,
                GateReversionReason: reason);
        }

        // Test gate.
        var testOutcome = await _gate.TestAsync(normalizedRoot, ct).ConfigureAwait(false);
        if (!testOutcome.Passed)
        {
            var revertFailed = await RevertAsync(backups).ConfigureAwait(false);
            var reason = AppendRevertFailures(
                $"tests broke after auto-apply: {testOutcome.FailureSummary ?? "no detail"}",
                revertFailed);
            _logger.Warning("auto-apply: {Reason}; reverted {Count} files ({Failed} failed)", reason, backups.Count, revertFailed);
            return new AutoApplyResult(
                Applied: Array.Empty<ConsolidatedFinding>(),
                Demoted: applied.Select(f => WithDemotion(f, reason)).Concat(demoted).ToList(),
                BuildTestGatePassed: false,
                GateReversionReason: reason);
        }

        return new AutoApplyResult(applied, demoted, BuildTestGatePassed: true, GateReversionReason: null);
    }

    // ---------- Per-finding apply ----------

    private async Task<string?> TryApplySingleAsync(
        ConsolidatedFinding finding,
        string normalizedRoot,
        IDictionary<string, byte[]?> backups,
        IReadOnlySet<string>? allowedScope,
        CancellationToken ct)
    {
        // Return value: null = applied successfully; non-null = demotion reason.

        if (finding.SuggestedFix is null)
            return "fix not auto-applicable: SuggestedFix is null";

        var fix = finding.SuggestedFix;
        if (fix.Kind == FixKind.Manual)
            return "fix not auto-applicable: kind=Manual";

        if (string.IsNullOrWhiteSpace(finding.File))
            return "fix not auto-applicable: File is empty";

        // Defensive confidence + size gates. The coordinator prompt also enforces these, but a
        // misbehaving or out-of-date coordinator could emit auto_apply for an out-of-spec
        // finding; the host re-checks so the gate is reliable regardless of prompt drift.
        if (finding.Confidence < _options.ConfidenceFloor)
            return $"reviewer confidence {finding.Confidence:0.00} is below floor {_options.ConfidenceFloor:0.00}";

        if (finding.DispositionConfidence < _options.ConfidenceFloor)
            return $"coordinator disposition_confidence {finding.DispositionConfidence:0.00} is below floor {_options.ConfidenceFloor:0.00}";

        var lineCount = CountLines(fix.OldText) + CountLines(fix.NewText);
        if (lineCount > _options.MaxLinesPerFinding)
            return $"fix touches {lineCount} lines, exceeds MaxLinesPerFinding {_options.MaxLinesPerFinding}";

        // Path safety. sec-004: use Path.GetRelativePath rather than a StartsWith(root) check so
        // case-insensitive filesystems (Windows NTFS) don't reject a valid path whose casing
        // disagrees with the user-supplied root. GetRelativePath uses the platform's path-equality
        // semantics; we then refuse anything that escapes via `..` or remains rooted (different
        // drive on Windows).
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, finding.File)); }
        catch (ArgumentException) { return "target file path is malformed"; }

        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return "target file outside repo root";
        }

        // C4: hard denylist. Even an in-scope, high-confidence finding is refused if it would write
        // to version-control internals, CI workflow definitions, or build-system files — the
        // executor's own gate then runs `dotnet build`, so a planted .targets/.csproj or a
        // .git/config `core.fsmonitor` payload would execute under the host account. Route to user.
        var denyReason = DenylistReason(relative);
        if (denyReason is not null) return denyReason;

        // C4: scope constraint. When the caller passed the resolved review scope, the target must
        // be one of the files that scope actually covered. A fix touching a file outside the
        // reviewed set (a common indirect-injection payload) is handed to the user, not applied.
        if (allowedScope is not null && !allowedScope.Contains(NormalizeRelative(relative)))
            return "target file is outside the reviewed scope";

        if (fix.Kind == FixKind.Edit)
        {
            if (string.IsNullOrEmpty(fix.OldText) || string.IsNullOrEmpty(fix.NewText))
                return "fix not auto-applicable: Edit requires non-empty OldText and NewText";

            if (!File.Exists(fullPath)) return "target file does not exist";

            // Snapshot original bytes before any modification (only on first touch).
            if (!backups.ContainsKey(fullPath))
                backups[fullPath] = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);

            var current = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);

            // Line-ending normalization. Windows checkouts with core.autocrlf=true store files as
            // CRLF on disk; an LLM-emitted suggested_fix almost always uses LF. Without this
            // adjustment every multi-line Edit on Windows would demote with "source diverged".
            // Strategy: if the file's predominant convention disagrees with OldText/NewText,
            // convert OldText + NewText to match the file's convention so writes preserve it.
            var (oldText, newText) = NormalizeLineEndingsToFile(current, fix.OldText, fix.NewText);

            var firstIdx = current.IndexOf(oldText, StringComparison.Ordinal);
            if (firstIdx < 0) return "source diverged: OldText not found in target file";

            // Ambiguity check: if OldText appears more than once, the fix isn't unambiguously
            // applicable. Refuse rather than guess.
            var secondIdx = current.IndexOf(oldText, firstIdx + 1, StringComparison.Ordinal);
            if (secondIdx >= 0) return "source diverged: OldText is ambiguous (appears more than once)";

            var next = string.Concat(current.AsSpan(0, firstIdx), newText, current.AsSpan(firstIdx + oldText.Length));
            await File.WriteAllTextAsync(fullPath, next, ct).ConfigureAwait(false);
            return null;
        }

        if (fix.Kind == FixKind.ReplaceFile)
        {
            if (string.IsNullOrEmpty(fix.NewText))
                return "fix not auto-applicable: ReplaceFile requires non-empty NewText";

            // Snapshot: bytes when the file exists, null when it doesn't (revert means delete).
            if (!backups.ContainsKey(fullPath))
            {
                backups[fullPath] = File.Exists(fullPath)
                    ? await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false)
                    : null;
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullPath, fix.NewText, ct).ConfigureAwait(false);
            return null;
        }

        return $"fix not auto-applicable: unknown FixKind {fix.Kind}";
    }

    private async Task<int> RevertAsync(IDictionary<string, byte[]?> backups)
    {
        // Once we've committed to reverting, do NOT honor caller cancellation. A partial revert
        // (some files restored, others left edited) leaves the working tree in a mixed state
        // worse than the gate failure itself. The user's Ctrl-C is too late at this point; the
        // alternative is silent corruption. Each File.WriteAllBytesAsync gets
        // CancellationToken.None so a canceled outer token doesn't fail-fast every write.
        // Per-file IO errors are still tolerated (try/catch) because one wedged file shouldn't
        // abort the rest of the rollback. sec-007: each per-file failure is logged with the
        // path + exception type so a partial-revert state is visible (not silently swallowed),
        // and the count is returned so callers can surface it in GateReversionReason.
        var failed = 0;
        foreach (var (path, original) in backups)
        {
            try
            {
                if (original is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    await File.WriteAllBytesAsync(path, original, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Warning(ex, "auto-apply revert: failed to restore {Path}", path);
            }
        }
        return failed;
    }

    /// <summary>
    /// Append a "; revert had N failures - check log" tail to a gate-failure reason when the
    /// revert wasn't clean (sec-007). Surfaces partial-revert state to the user via
    /// <see cref="AutoApplyResult.GateReversionReason"/> instead of silently swallowing it.
    /// </summary>
    internal static string AppendRevertFailures(string baseReason, int revertFailed) =>
        revertFailed > 0
            ? $"{baseReason}; revert had {revertFailed} per-file failure(s) - check log"
            : baseReason;

    private static ConsolidatedFinding WithDemotion(ConsolidatedFinding f, string reason)
    {
        var note = string.IsNullOrEmpty(f.ConsolidatorNote) ? reason : $"{f.ConsolidatorNote} | demoted: {reason}";
        return f with
        {
            Disposition = Disposition.NeedsUser,
            ConsolidatorNote = note,
            RejectionReason = null,
        };
    }

    /// <summary>
    /// Normalize a repo-root-relative path to the form used for scope-set membership: forward
    /// slashes, no leading separator. Matches <c>ReviewFile.RelativePath</c>'s normalization.
    /// </summary>
    internal static string NormalizeRelative(string relative) =>
        relative.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// C4 hard denylist. Returns a demotion reason when <paramref name="relative"/> (a
    /// repo-root-relative, in-root path) targets version-control internals, a CI workflow
    /// definition, or a build-system file — all of which would let a model-emitted "fix" execute
    /// code under the host account once the post-apply build/test gate runs. Returns null when the
    /// path is allowed. Public for tests; unconditional (applies even when no scope is supplied).
    /// </summary>
    internal static string? DenylistReason(string relative)
    {
        var norm = NormalizeRelative(relative);
        var segments = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // .git/ internals — hooks/, config (core.fsmonitor → arbitrary command on every git op), etc.
        // Match a `.git` segment at ANY depth (sec-101): a nested .git/ — a submodule or a vendored
        // repo (e.g. submodule/.git/config, vendor/repo/.git/hooks/pre-commit) — is just as dangerous
        // and was writable under the old segments[0]-only check. A path component equal exactly to
        // ".git" is always a git directory, so the any-depth match has no legitimate false positives.
        if (segments.Any(s => s.Equals(".git", FileSystemNaming.Comparison)))
            return "auto-apply refused: target is under a .git/ directory (version-control internals)";

        // CI workflow definitions run on push under the repo's credentials.
        if (segments.Length >= 2
            && segments[0].Equals(".github", FileSystemNaming.Comparison)
            && segments[1].Equals("workflows", FileSystemNaming.Comparison))
            return "auto-apply refused: target is a CI workflow file under .github/workflows/";

        // NuGet/SDK config files steer the gate's `dotnet build` restore: a planted nuget.config
        // can redirect package sources to an attacker-controlled feed (arbitrary code on restore),
        // and global.json can pin a malicious/unexpected SDK. Matching is by filename (not extension),
        // using the filesystem's casing rule — the same rule `dotnet restore` uses to resolve them.
        var fileName = Path.GetFileName(norm);
        if (fileName.Equals("nuget.config", FileSystemNaming.Comparison)
            || fileName.Equals("global.json", FileSystemNaming.Comparison))
            return $"auto-apply refused: target is a NuGet/SDK config file ({fileName})";

        // Build-system files are executed as MSBuild logic during the gate's `dotnet build`.
        var ext = Path.GetExtension(norm);
        if (!string.IsNullOrEmpty(ext))
        {
            foreach (var denied in BuildFileExtensions)
            {
                if (ext.Equals(denied, FileSystemNaming.Comparison))
                    return $"auto-apply refused: target is a build-system file ({ext})";
            }
        }

        return null;
    }

    /// <summary>
    /// File extensions whose content is executed by the build toolchain (MSBuild project/import
    /// files and solution files). Auto-applying these is refused because the executor itself then
    /// runs <c>dotnet build</c> against the modified tree.
    /// </summary>
    private static readonly string[] BuildFileExtensions =
        [".csproj", ".props", ".targets", ".slnx", ".sln", ".fsproj", ".vbproj"];

    /// <summary>
    /// Adjust <paramref name="oldText"/> and <paramref name="newText"/> so their line endings
    /// match the predominant convention of <paramref name="current"/>. Without this, an LLM that
    /// emits LF-only text against a Windows CRLF checkout would never match the source.
    ///
    /// <para>
    /// Heuristic: if any <c>\r\n</c> appears in <paramref name="current"/>, the file is treated
    /// as CRLF and any lone <c>\n</c> in oldText/newText is upgraded. Otherwise the file is
    /// treated as LF and any <c>\r\n</c> in oldText/newText is downgraded. Mixed-line-ending
    /// source files lose: they'll be normalized to whichever convention dominates. Acceptable
    /// for v1 (mixed line endings in a single .cs file is itself a project-convention
    /// violation).
    /// </para>
    /// </summary>
    internal static (string OldText, string NewText) NormalizeLineEndingsToFile(string current, string oldText, string newText)
    {
        var fileIsCrlf = current.Contains("\r\n", StringComparison.Ordinal);

        if (fileIsCrlf)
        {
            // Upgrade lone \n to \r\n. The two-step replace avoids creating \r\r\n out of existing \r\n.
            if (oldText.Contains('\n') && !LooksAlreadyCrlf(oldText))
                oldText = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (newText.Contains('\n') && !LooksAlreadyCrlf(newText))
                newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        else
        {
            // Downgrade \r\n to \n.
            if (oldText.Contains("\r\n", StringComparison.Ordinal))
                oldText = oldText.Replace("\r\n", "\n");
            if (newText.Contains("\r\n", StringComparison.Ordinal))
                newText = newText.Replace("\r\n", "\n");
        }
        return (oldText, newText);
    }

    /// <summary>
    /// Count the number of lines in <paramref name="text"/>. A null or empty string counts as 0;
    /// a single line with no terminator counts as 1; <c>\n</c>-terminated lines count one per
    /// newline. CRLF counted as one line break, not two.
    /// </summary>
    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
            // Skip the \r in \r\n so we don't double-count.
        }
        return lines;
    }

    private static bool LooksAlreadyCrlf(string s)
    {
        // True when every \n in `s` is preceded by \r. We compare counts as a cheap proxy: if
        // every lone-\n is paired, the string is already CRLF and we should leave it alone.
        var lf = 0;
        var crlf = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                lf++;
                if (i > 0 && s[i - 1] == '\r') crlf++;
            }
        }
        return lf > 0 && lf == crlf;
    }
}
