namespace InfiniContext.Core.Review;

/// <summary>
/// Spawns a one-shot full-model <c>claude</c> child to run a single review turn. Distinct from
/// <see cref="LightModel.ILightModelService"/>: the user's design says reviewers must use the
/// user's main reasoning model (Sonnet/Opus class), not the Haiku-class light model. The
/// coordinator passes each reviewer a self-contained prompt with all needed context inlined тАФ
/// the agent runs with no tools, no MCP, no slash-commands, in an isolated managed cwd.
///
/// <para>
/// The host spawns five concurrent <see cref="RunOneShotAsync"/> calls (one per reviewer role)
/// plus a sixth for the coordinator. Each invocation gets a fresh process; no long-lived sharing
/// between calls тАФ keeps the failure blast radius to one reviewer and avoids context leakage.
/// </para>
/// </summary>
public interface IReviewerAgentService
{
    /// <summary>
    /// Run one turn with <paramref name="systemPrompt"/> in the system slot and
    /// <paramref name="userPayload"/> as the user message. Returns the assistant's full response
    /// text (the caller is responsible for JSON extraction via <see cref="FindingJson.ExtractJson"/>).
    ///
    /// <para>
    /// Throws on stream EOF before the <c>result</c> event, on a <c>result</c> with
    /// <c>is_error=true</c>, or when the per-invocation timeout fires.
    /// </para>
    /// </summary>
    Task<string> RunOneShotAsync(string systemPrompt, string userPayload, CancellationToken ct);
}
