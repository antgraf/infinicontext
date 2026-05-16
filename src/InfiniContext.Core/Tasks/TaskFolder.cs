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
    /// Files referenced from the first column of the <c>index.md</c> markdown table. Returns
    /// an empty list if the index doesn't exist or has no rows. Header rows (literal
    /// <c>File</c>) and the separator row (<c>---</c>) are filtered out. Case is preserved.
    /// </summary>
    public async Task<IReadOnlyList<string>> IndexedFilesAsync(CancellationToken ct = default)
    {
        var indexPath = System.IO.Path.Combine(Path, ArtifactTemplates.IndexFileName);
        if (!File.Exists(indexPath)) return Array.Empty<string>();

        var lines = await File.ReadAllLinesAsync(indexPath, ct).ConfigureAwait(false);
        var files = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            if (!line.StartsWith('|')) continue;

            // Take the content of the first cell.
            var firstPipe = 1; // skip leading '|'
            var nextPipe = line.IndexOf('|', firstPipe);
            if (nextPipe < 0) continue;
            var firstCell = line[firstPipe..nextPipe].Trim();

            if (firstCell.Length == 0) continue;
            if (firstCell.Equals("File", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsSeparatorCell(firstCell)) continue;

            files.Add(firstCell);
        }
        return files;
    }

    /// <summary>
    /// Files present on disk but not referenced from <c>index.md</c>. <c>index.md</c> itself is
    /// always excluded (it's the index, not an indexed entry). Result is sorted ordinal.
    /// </summary>
    public async Task<IReadOnlyList<string>> DetectDriftAsync(CancellationToken ct = default)
    {
        var artifacts = await ListArtifactsAsync(ct).ConfigureAwait(false);
        var indexed = (await IndexedFilesAsync(ct).ConfigureAwait(false))
            .ToHashSet(FileSystemNaming.Comparer);

        var drift = new List<string>();
        foreach (var info in artifacts)
        {
            if (info.Name.Equals(ArtifactTemplates.IndexFileName, FileSystemNaming.Comparison)) continue;
            if (!indexed.Contains(info.Name)) drift.Add(info.Name);
        }
        drift.Sort(StringComparer.Ordinal);
        return drift;
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
