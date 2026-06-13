namespace InfiniContext.Core.Tasks;

/// <summary>
/// One <c>.task/&lt;id&gt;-&lt;slug&gt;/</c> folder under a task's <c>home_cwd</c>. Pure I/O — no
/// orchestration, no SQLite. Higher-level operations (creation, fork, threshold-driven flush)
/// live in <c>TaskService</c>.
///
/// <para>
/// All paths are constructed via <see cref="Path.Combine(string,string,string)"/>; never with
/// string concatenation, per the cross-platform rule in <c>CLAUDE.md</c>.
/// </para>
/// </summary>
public sealed class TaskFolder
{
    public string HomeCwd { get; }
    public int TaskId { get; }
    public string Slug { get; }

    /// <summary>Folder name as it appears under <c>.task/</c>, e.g. <c>0042-implement-foo</c>.</summary>
    public string Name { get; }

    /// <summary>Absolute path to the task folder.</summary>
    public string Path { get; }

    public TaskFolder(string homeCwd, int taskId, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeCwd);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (taskId <= 0) throw new ArgumentOutOfRangeException(nameof(taskId), "taskId must be a positive integer.");

        HomeCwd = homeCwd;
        TaskId = taskId;
        Slug = slug;
        Name = $"{taskId:D4}-{slug}";
        Path = System.IO.Path.Combine(homeCwd, ".task", Name);
    }

    /// <summary>
    /// Create the folder if missing and write the three mandatory scaffolds (<c>index.md</c>,
    /// <c>description.md</c>, <c>progress.md</c>) for any that don't yet exist. Existing files
    /// are never overwritten — repeat calls are idempotent.
    /// </summary>
    public async Task EnsureExistsAsync(DateOnly today, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path);
        await WriteIfMissingAsync(ArtifactTemplates.IndexFileName, ArtifactTemplates.IndexMarkdown(today), ct).ConfigureAwait(false);
        await WriteIfMissingAsync(ArtifactTemplates.DescriptionFileName, ArtifactTemplates.DescriptionPlaceholder(), ct).ConfigureAwait(false);
        await WriteIfMissingAsync(ArtifactTemplates.ProgressFileName, ArtifactTemplates.ProgressMarkdown(today), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Overwrite <c>description.md</c> with caller-supplied content. Used after the light model
    /// generates the initial description from the user's free-form input. Caller is responsible
    /// for ensuring the folder exists (call <see cref="EnsureExistsAsync"/> first).
    /// </summary>
    public async Task WriteScaffoldedDescriptionAsync(string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = System.IO.Path.Combine(Path, ArtifactTemplates.DescriptionFileName);
        await File.WriteAllTextAsync(target, content, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerate every <c>*.md</c> file directly under the task folder (no recursion — there
    /// are no subfolders by design). Returns names sorted case-insensitively.
    /// </summary>
    public Task<IReadOnlyList<ArtifactInfo>> ListArtifactsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Path))
            return Task.FromResult<IReadOnlyList<ArtifactInfo>>(Array.Empty<ArtifactInfo>());

        var infos = new List<ArtifactInfo>();
        foreach (var file in Directory.EnumerateFiles(Path, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(file);
            var lastWriteUtc = File.GetLastWriteTimeUtc(file);
            infos.Add(new ArtifactInfo(name, lastWriteUtc));
        }
        infos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<ArtifactInfo>>(infos);
    }

    /// <summary>
    /// Files referenced from the first column of the canonical <c>index.md</c> markdown table.
    /// Returns an empty list if the index doesn't exist or has no rows. Case is preserved.
    ///
    /// <para>
    /// Parsing is <em>anchored</em> to the canonical
    /// <c>| File | Description | Last updated |</c> header (M17): only rows in the contiguous
    /// table block following that header are treated as file references. Without the anchor, the
    /// first cell of <em>any</em> markdown table row counts as a file, so an unrelated table
    /// elsewhere in the index (or in a hand-written note) silently masks drift.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> IndexedFilesAsync(CancellationToken ct = default)
    {
        var indexPath = System.IO.Path.Combine(Path, ArtifactTemplates.IndexFileName);
        if (!File.Exists(indexPath)) return Array.Empty<string>();

        var lines = await File.ReadAllLinesAsync(indexPath, ct).ConfigureAwait(false);
        var files = new List<string>();
        var inTable = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            if (!line.StartsWith('|'))
            {
                // A non-table line ends the canonical block. (A blank line between the header and
                // its rows is not expected in a well-formed table, so this is safe.)
                if (inTable) break;
                continue;
            }

            var firstCell = FirstCell(line);
            if (firstCell is null) continue;

            if (!inTable)
            {
                // Look for the canonical header row to start the block. Until we see it, ignore
                // any stray table rows.
                if (IsCanonicalHeader(line)) inTable = true;
                continue;
            }

            // Inside the canonical table: skip the separator row, collect file cells.
            if (firstCell.Length == 0 || IsSeparatorCell(firstCell)) continue;
            files.Add(firstCell);
        }
        return files;
    }

    /// <summary>
    /// Bidirectional drift between the task folder and its <c>index.md</c> (M17):
    /// <see cref="DriftReport.UnindexedFiles"/> = <c>*.md</c> on disk not referenced from the
    /// index; <see cref="DriftReport.MissingFiles"/> = index rows pointing at files that no
    /// longer exist on disk. Reporting only the first direction lets a stale index row (a file
    /// was deleted but its row left behind) hand a fresh session a misleading table of contents.
    /// <c>index.md</c> itself is excluded from the on-disk side (it's the index, not an entry).
    /// Both lists are sorted ordinal.
    /// </summary>
    public async Task<DriftReport> DetectDriftReportAsync(CancellationToken ct = default)
    {
        var artifacts = await ListArtifactsAsync(ct).ConfigureAwait(false);
        var indexedList = await IndexedFilesAsync(ct).ConfigureAwait(false);
        var indexed = indexedList.ToHashSet(FileSystemNaming.Comparer);
        var onDisk = artifacts.Select(a => a.Name).ToHashSet(FileSystemNaming.Comparer);

        var unindexed = new List<string>();
        foreach (var info in artifacts)
        {
            if (info.Name.Equals(ArtifactTemplates.IndexFileName, FileSystemNaming.Comparison)) continue;
            if (!indexed.Contains(info.Name)) unindexed.Add(info.Name);
        }
        unindexed.Sort(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var name in indexedList)
        {
            if (name.Equals(ArtifactTemplates.IndexFileName, FileSystemNaming.Comparison)) continue;
            if (!onDisk.Contains(name)) missing.Add(name);
        }
        missing.Sort(StringComparer.Ordinal);

        return new DriftReport(unindexed, missing);
    }

    /// <summary>
    /// Files present on disk but not referenced from <c>index.md</c> (the unindexed direction of
    /// <see cref="DetectDriftReportAsync"/>). Retained for callers that only need that direction.
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectDriftAsync(CancellationToken ct = default)
        => (await DetectDriftReportAsync(ct).ConfigureAwait(false)).UnindexedFiles;

    private static string? FirstCell(string trimmedLine)
    {
        // trimmedLine starts with '|'. Take the content of the first cell.
        var nextPipe = trimmedLine.IndexOf('|', 1);
        if (nextPipe < 0) return null;
        return trimmedLine[1..nextPipe].Trim();
    }

    private static bool IsCanonicalHeader(string trimmedLine)
    {
        // Match `| File | Description | Last updated |` case-insensitively on the three header
        // cells, tolerating extra spacing. Splitting on '|' yields a leading/trailing empty cell.
        var cells = trimmedLine.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
        return cells.Length == 3
            && cells[0].Equals("File", StringComparison.OrdinalIgnoreCase)
            && cells[1].Equals("Description", StringComparison.OrdinalIgnoreCase)
            && cells[2].Equals("Last updated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copy every <c>*.md</c> file from this folder to <paramref name="destination"/>,
    /// overwriting same-named files in the destination. Source mtimes are preserved on the
    /// copies so fork lineage stays auditable. The destination folder is created if missing.
    /// </summary>
    public async Task CopyArtifactsToAsync(TaskFolder destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(this, destination))
            throw new ArgumentException("Cannot copy a TaskFolder onto itself.", nameof(destination));

        Directory.CreateDirectory(destination.Path);

        if (!Directory.Exists(Path)) return;

        foreach (var srcFile in Directory.EnumerateFiles(Path, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(srcFile);
            var dstFile = System.IO.Path.Combine(destination.Path, name);

            // Use streaming copy so large files don't all land in memory at once.
            await using (var src = File.OpenRead(srcFile))
            await using (var dst = File.Create(dstFile))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(dstFile, File.GetLastWriteTimeUtc(srcFile));
        }
    }

    private async Task WriteIfMissingAsync(string fileName, string content, CancellationToken ct)
    {
        var target = System.IO.Path.Combine(Path, fileName);
        if (File.Exists(target)) return;
        await File.WriteAllTextAsync(target, content, ct).ConfigureAwait(false);
    }

    private static bool IsSeparatorCell(string cell)
    {
        // Markdown separator cells contain only '-' / ':' (and possibly leading/trailing spaces,
        // already trimmed by the caller).
        foreach (var c in cell)
            if (c != '-' && c != ':') return false;
        return cell.Length > 0;
    }
}

/// <summary>
/// One <c>.md</c> file under a task folder. Used by <see cref="TaskFolder.ListArtifactsAsync"/>.
/// </summary>
/// <param name="Name">File name (no path).</param>
/// <param name="LastModifiedUtc">Last-write timestamp in UTC.</param>
public sealed record ArtifactInfo(string Name, DateTime LastModifiedUtc);

/// <summary>
/// Bidirectional drift between a task folder and its <c>index.md</c>
/// (<see cref="TaskFolder.DetectDriftReportAsync"/>).
/// </summary>
/// <param name="UnindexedFiles"><c>*.md</c> on disk not referenced from <c>index.md</c>.</param>
/// <param name="MissingFiles">Index rows pointing at files that no longer exist on disk.</param>
public sealed record DriftReport(
    IReadOnlyList<string> UnindexedFiles,
    IReadOnlyList<string> MissingFiles)
{
    /// <summary>True when either direction has at least one entry.</summary>
    public bool HasDrift => UnindexedFiles.Count > 0 || MissingFiles.Count > 0;
}
