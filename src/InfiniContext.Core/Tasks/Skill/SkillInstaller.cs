using Serilog;

namespace InfiniContext.Core.Tasks.Skill;

/// <summary>
/// Installs <see cref="InfiniContextTaskSkill"/> into <c>~/.claude/skills/infinicontext-task/SKILL.md</c>
/// on host startup. Idempotent: if the file is already present and its embedded version stamp
/// is at least <see cref="InfiniContextTaskSkill.Version"/>, this is a no-op. When an overwrite
/// happens the previous content is copied to <c>SKILL.md.bak</c> (single slot, overwritten on
/// every rewrite) so a user who customised the file can recover.
/// </summary>
public static class SkillInstaller
{
    public const string BackupSuffix = ".bak";

    public enum InstallResult
    {
        /// <summary>The file did not exist; it has been created.</summary>
        Installed,
        /// <summary>The file exists at the current version. No write occurred.</summary>
        UpToDate,
        /// <summary>The file existed at an older version (or had no marker); it has been overwritten.</summary>
        Updated,
    }

    /// <summary>
    /// Install or refresh the skill on disk. Pass <paramref name="userProfilePath"/> to override
    /// the install root (tests use a temp dir). Returns the action taken so the caller can log it.
    /// </summary>
    public static async Task<InstallResult> InstallIfMissingOrOutdatedAsync(
        string? userProfilePath = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var skillDir = InfiniContextTaskSkill.GetInstallDir(userProfilePath);
        var skillFile = InfiniContextTaskSkill.GetSkillFilePath(userProfilePath);
        var log = logger ?? Log.Logger;

        if (!File.Exists(skillFile))
        {
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(skillFile, InfiniContextTaskSkill.SkillMarkdown, ct).ConfigureAwait(false);
            return InstallResult.Installed;
        }

        var existing = await File.ReadAllTextAsync(skillFile, ct).ConfigureAwait(false);
        var hadMarker = TryParseVersion(existing, out var onDiskVersion);
        if (hadMarker && onDiskVersion >= InfiniContextTaskSkill.Version)
            return InstallResult.UpToDate;

        // File exists but version is older or absent. Back up the existing content first so
        // any user edits are recoverable, then overwrite. The backup is single-slot — each
        // rewrite stomps the previous .bak — but that's enough for "I just upgraded and lost
        // my customisation" recovery without bloating the install dir.
        Directory.CreateDirectory(skillDir);
        var backupFile = skillFile + BackupSuffix;
        try
        {
            await File.WriteAllTextAsync(backupFile, existing, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Backup is best-effort; we still proceed with the overwrite so the rules stay
            // fresh, but the user gets a warning so they know to check whether they had edits.
            log.Warning(ex, "skill: failed to write backup at {BackupFile}; proceeding with overwrite", backupFile);
        }

        await File.WriteAllTextAsync(skillFile, InfiniContextTaskSkill.SkillMarkdown, ct).ConfigureAwait(false);
        log.Information(
            "skill: overwrote SKILL.md (previous version: {Previous}, new version: {Current}); previous content saved at {BackupFile}",
            hadMarker ? onDiskVersion.ToString() : "<none>",
            InfiniContextTaskSkill.Version,
            backupFile);
        return InstallResult.Updated;
    }

    /// <summary>
    /// Parse the embedded version marker from existing SKILL.md content. Returns <c>false</c>
    /// when the marker is absent or malformed; <see cref="InfiniContextTaskSkill.Version"/>
    /// comparisons treat absent as "outdated", which forces a rewrite.
    /// </summary>
    public static bool TryParseVersion(string content, out int version)
    {
        version = 0;
        if (string.IsNullOrEmpty(content)) return false;

        var prefix = InfiniContextTaskSkill.VersionMarkerPrefix;
        var idx = content.LastIndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return false;

        var start = idx + prefix.Length;
        var end = content.IndexOf(InfiniContextTaskSkill.VersionMarkerSuffix, start, StringComparison.Ordinal);
        if (end < 0) return false;

        var raw = content.AsSpan(start, end - start).Trim();
        return int.TryParse(raw, out version);
    }
}
