namespace InfiniContext.Core.Tasks;

/// <summary>
/// Strategies for <see cref="TaskService.MergeAsync"/>. MVP ships only
/// <see cref="DeterministicConcat"/>; the other two are documented placeholders that throw
/// <see cref="NotImplementedException"/> until their respective post-MVP work lands.
///
/// <para>
/// Per the design's "merge tiers, default to full-model session" decision, a richer
/// <see cref="FullModelSession"/> path will eventually become the default once it's safe — but
/// MVP intentionally keeps the only working tier explicit, so callers know they're getting a
/// mechanical concat and not a semantic merge.
/// </para>
/// </summary>
public enum MergeTier
{
    /// <summary>
    /// Lossless mechanical concatenation: the merged folder contains, for each filename present
    /// in any source, a single file with each source's content stitched together under
    /// labelled section dividers. No semantic understanding; safe for trivial cases only.
    /// </summary>
    DeterministicConcat,

    /// <summary>Light-model-driven merge. Post-MVP.</summary>
    LightModel,

    /// <summary>Full-model session that reads and decides which content is authoritative. Post-MVP.</summary>
    FullModelSession,
}
