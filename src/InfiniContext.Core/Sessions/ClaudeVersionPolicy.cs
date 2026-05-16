namespace InfiniContext.Core.Sessions;

/// <summary>
/// Version policy for the spawned <c>claude</c> binary, per design doc §7.3.
///
/// <para>
/// <see cref="MinClaudeVersion"/> is the hard floor — observing a lower version refuses to
/// continue, since the parser/state-machine assumptions don't hold below this. Mismatch
/// against <see cref="TestedAgainstVersion"/> is a soft warning: drift is the load-bearing
/// signal, but a known-good version pin gives the maintainer a starting line.
/// </para>
///
/// <para>
/// The check runs against the <c>claude_code_version</c> field on the first <c>system/init</c>
/// event. <see cref="Compare"/> tolerates non-SemVer suffixes (e.g. <c>2.1.112-beta.4</c>) by
/// comparing only the leading numeric triplet.
/// </para>
/// </summary>
public static class ClaudeVersionPolicy
{
    /// <summary>The minimum <c>claude</c> version this host is known to be parser-compatible with.</summary>
    public const string MinClaudeVersion = "2.0.0";

    /// <summary>The <c>claude</c> version that Stage 0 fixtures were recorded against. Mismatch logs a warning.</summary>
    public const string TestedAgainstVersion = "2.1.112";

    public enum Outcome
    {
        /// <summary>Versions matched the tested-against pin exactly.</summary>
        ExactMatch,
        /// <summary>Higher than tested-against but ≥ minimum. Soft warning recommended.</summary>
        DriftAbove,
        /// <summary>Lower than tested-against but ≥ minimum. Soft warning recommended.</summary>
        DriftBelow,
        /// <summary>Below the hard floor. Host should refuse to continue.</summary>
        BelowMinimum,
        /// <summary>Could not parse the reported version. Treated as a soft warning.</summary>
        Unrecognised,
    }

    /// <summary>
    /// Classify the observed claude version relative to <see cref="MinClaudeVersion"/> and
    /// <see cref="TestedAgainstVersion"/>. Returns <see cref="Outcome.Unrecognised"/> if
    /// <paramref name="observed"/> can't be parsed.
    /// </summary>
    public static Outcome Check(string? observed) =>
        Check(observed, MinClaudeVersion, TestedAgainstVersion);

    /// <summary>
    /// Same as <see cref="Check(string?)"/> but with caller-supplied min/tested versions, sourced
    /// from <c>settings.json</c> at runtime. Both bounds must parse; if either is malformed the
    /// observed version is reported as <see cref="Outcome.Unrecognised"/>.
    /// </summary>
    public static Outcome Check(string? observed, string minVersion, string testedAgainstVersion)
    {
        if (string.IsNullOrWhiteSpace(observed)) return Outcome.Unrecognised;
        if (!TryParse(observed, out var v)) return Outcome.Unrecognised;
        if (!TryParse(minVersion, out var min)) return Outcome.Unrecognised;
        if (!TryParse(testedAgainstVersion, out var tested)) return Outcome.Unrecognised;

        if (Compare(v, min) < 0) return Outcome.BelowMinimum;
        var rel = Compare(v, tested);
        if (rel == 0) return Outcome.ExactMatch;
        return rel > 0 ? Outcome.DriftAbove : Outcome.DriftBelow;
    }

    /// <summary>Parses the leading numeric triplet, ignoring any pre-release / build-metadata suffix.</summary>
    public static bool TryParse(string version, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version)) return false;

        // Trim a leading 'v' (e.g. "v2.1.112") and split off the first non-version delimiter.
        var s = version.TrimStart('v', 'V');
        var end = s.AsSpan().IndexOfAny('-', '+');
        if (end >= 0) s = s[..end];

        var parts = s.Split('.');
        if (parts.Length < 1) return false;

        int major = 0, minor = 0, patch = 0;
        if (!int.TryParse(parts[0], out major)) return false;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch)) return false;

        parsed = (major, minor, patch);
        return true;
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }
}
