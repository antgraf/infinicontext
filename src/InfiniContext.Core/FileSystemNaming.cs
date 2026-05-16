namespace InfiniContext.Core;

/// <summary>
/// Centralizes the "how do we compare filenames?" rule. Windows file systems (NTFS, FAT) are
/// case-insensitive, so <c>Foo.md</c> and <c>foo.md</c> identify the same artifact and we must
/// dedupe them. macOS and Linux file systems are case-preserving but typically case-sensitive
/// (APFS defaults to case-insensitive but supports case-sensitive volumes; ext4/HFS+ vary), so
/// <c>Foo.md</c> and <c>foo.md</c> can coexist as distinct files and must be treated as such.
///
/// <para>
/// Stage 2B Phase 7 (closes 2A todo §B C7): the platform gate is centralized here so a future
/// reviewer never has to spot-fix one call site at a time. All filename equality checks and set
/// keys that decide "is artifact X already accounted for?" should route through
/// <see cref="Comparison"/> or <see cref="Comparer"/>.
/// </para>
///
/// <para>
/// Sort-only callers (display ordering, table rendering) intentionally do <em>not</em> use this —
/// a stable cross-platform ordering is friendlier to users than an OS-dependent one. The
/// distinction is identity (gated) vs ordering (not gated).
/// </para>
/// </summary>
public static class FileSystemNaming
{
    /// <summary>
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> on Windows, <see cref="StringComparison.Ordinal"/>
    /// everywhere else. Use for filename equality checks.
    /// </summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> on Windows, <see cref="StringComparer.Ordinal"/>
    /// everywhere else. Use as the key comparer for dictionaries / hash sets keyed by filename.
    /// </summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
