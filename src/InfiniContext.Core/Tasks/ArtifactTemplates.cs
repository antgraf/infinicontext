namespace InfiniContext.Core.Tasks;

/// <summary>
/// Markdown scaffolds for the three mandatory task artifacts. The scaffolds are intentionally
/// terse — they're stand-ins until the agent (or, for <c>description.md</c>, the light model)
/// fills in real content. <c>EnsureExistsAsync</c> writes these only when the file is missing.
///
/// <para>
/// Per the design's mandatory/optional split: <c>index.md</c>, <c>description.md</c>, and
/// <c>progress.md</c> are mandatory; <c>plan.md</c>, <c>research.md</c>, <c>decisions.md</c>,
/// <c>design.md</c>, <c>issues.md</c> and others are optional and added by the agent on demand. Optional
/// artifacts get no scaffold here — the agent creates them with content.
/// </para>
/// </summary>
public static class ArtifactTemplates
{
    public const string IndexFileName = "index.md";
    public const string DescriptionFileName = "description.md";
    public const string ProgressFileName = "progress.md";

    public static string IndexMarkdown(DateOnly today) => $"""
        # Task index

        | File | Description | Last updated |
        |------|-------------|--------------|
        | {DescriptionFileName} | Full task spec | {today:yyyy-MM-dd} |
        | {ProgressFileName} | Current status and open questions | {today:yyyy-MM-dd} |
        """;

    public static string DescriptionPlaceholder() => """
        # Description

        _Not yet written. The light model populates this when the task is created;
        the agent refines it during work._
        """;

    public static string ProgressMarkdown(DateOnly today) => $"""
        # Progress

        ## {today:yyyy-MM-dd}

        - Task created.
        """;
}
