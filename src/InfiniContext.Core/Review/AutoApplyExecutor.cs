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
    private readonly ILogger _logger;

    public AutoApplyExecutor(IBuildTestRunner gate, ILogger? logger = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? Log.ForContext<AutoApplyExecutor>();
    }

    public async Task<AutoApplyResult> ApplyAsync(
        string repoRoot,
        IReadOnlyList<ConsolidatedFinding> findings,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(findings);
        if (!Directory.Exists(repoRoot)) throw new DirectoryNotFoundException($"Repo root does not exist: {repoRoot}");

        var normalizedRoot = Path.GetFullPath(repoRoot);

        // Baseline gate. If the build or test is already red BEFORE we apply anything, the
        // post-apply gate would either blame our edits for pre-existing failures (and revert
        // good work) or cover them up. Refuse the batch with a clear message instead. Only pay
        // the baseline cost when there's actually something to auto-apply.
        if (findings.Count > 0)
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
        // value is the original bytes (or null when the file didn't exist before the apply тАФ in
        // which case revert = delete).
        var backups = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var applied = new List<ConsolidatedFinding>();
        var demoted = new List<ConsolidatedFinding>();

        foreach (var finding in findings)
        {
            ct.ThrowIfCancellationRequested();

            var demotion = await TryApplySingleAsync(finding, normalizedRoot, backups, ct).ConfigureAwait(false);
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

        // Build gate.
        var buildOutcome = await _gate.BuildAsync(normalizedRoot, ct).ConfigureAwait(false);
        if (!buildOutcome.Passed)
        {
            await RevertAsync(backups).ConfigureAwait(false);
            var reason = $"build broke after auto-apply: {buildOutcome.FailureSummary ?? "no detail"}";
            _logger.Warning("auto-apply: {Reason}; reverted {Count} files", reason, backups.Count);
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
            await RevertAsync(backups).ConfigureAwait(false);
            var reason = $"tests broke after auto-apply: {testOutcome.FailureSummary ?? "no detail"}";
            _logger.Warning("auto-apply: {Reason}; reverted {Count} files", reason, backups.Count);
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

        // Path safety.
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, finding.File)); }
        catch (ArgumentException) { return "target file path is malformed"; }

        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, normalizedRoot, StringComparison.Ordinal))
        {
            return "target file outside repo root";
        }

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

    private static async Task RevertAsync(IDictionary<string, byte[]?> backups)
    {
        // Once we've committed to reverting, do NOT honor caller cancellation. A partial revert
        // (some files restored, others left edited) leaves the working tree in a mixed state
        // worse than the gate failure itself. The user's Ctrl-C is too late at this point; the
        // alternative is silent corruption. Each File.WriteAllBytesAsync gets
        // CancellationToken.None so a canceled outer token doesn't fail-fast every write.
        // Per-file IO errors are still tolerated (try/catch) because one wedged file shouldn't
        // abort the rest of the rollback.
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
            catch
            {
                // Best-effort: one file's IO failure shouldn't abort the rest of the revert.
            }
        }
    }

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
