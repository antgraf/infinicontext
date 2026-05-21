using InfiniContext.Core.Configuration;
using InfiniContext.Core.Sessions;

namespace InfiniContext.Core.Mcp;

/// <summary>
/// Approval sink for the <c>Ask</c> mode (Stage 2B Phase 5). Routes each request into the
/// session's <see cref="PendingApprovalSlot"/> instead of reading <c>Console.In</c> directly вЂ” the
/// host UI is the only thing that owns stdin (CLI) or input dispatch (GUI), and it routes the
/// user's verdict to the focused session's slot. This is what removes the W-16 race entirely (the
/// legacy <c>ConsoleApprovalSink</c> with its <c>Task.Run(Console.In.ReadLine)</c> is deleted, not
/// patched): the offending background read no longer exists.
///
/// <para>
/// Promoted from <c>InfiniContext.Cli</c> to Core in slice 4i so the GUI can wire the same
/// slot-backed flow without duplicating the type. CLI callers keep the original transcript-echo
/// behavior (each call writes a <c>[approval] tool=…</c> banner + verdict line to the session's
/// <see cref="TranscriptModel"/>, which the focused pane renders live and off-screen sessions
/// accumulate). GUI callers pass <paramref name="writeTranscriptBanner"/>=<c>false</c> so the
/// transcript stays clean of CLI-style markers вЂ” the GUI surfaces the same information through a
/// real banner UI bound to <see cref="PendingApprovalSlot.Changed"/>.
/// </para>
/// </summary>
public sealed class SlotApprovalSink : IApprovalSink
{
    private readonly PendingApprovalSlot _slot;
    private readonly TranscriptModel _transcript;
    private readonly TimeSpan _timeout;
    private readonly ApprovalCosmetics _cosmetics;
    private readonly bool _writeTranscriptBanner;

    public SlotApprovalSink(
        PendingApprovalSlot slot,
        TranscriptModel transcript,
        TimeSpan? timeout = null,
        ApprovalCosmetics? cosmetics = null,
        bool writeTranscriptBanner = true)
    {
        _slot = slot ?? throw new ArgumentNullException(nameof(slot));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _cosmetics = cosmetics ?? new ApprovalCosmetics();
        _writeTranscriptBanner = writeTranscriptBanner;
    }

    public async Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (_writeTranscriptBanner)
        {
            var summary = request.Input.ToJsonString();
            if (summary.Length > 240) summary = summary[..240] + "…";

            _transcript.Append($"\n[approval] tool={request.ToolName} input={summary}\n");
            _transcript.Append($"[approval] approve? (y/N, {_timeout.TotalSeconds:F0}s timeout)\n");
        }

        ApprovalResponse response;
        try
        {
            response = await _slot.RequestAsync(request, _timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_writeTranscriptBanner)
                _transcript.Append(_cosmetics.DeniedMessage + "\n");
            throw;
        }

        if (_writeTranscriptBanner)
        {
            var line = response.Behavior == ApprovalBehavior.Allow
                ? _cosmetics.ApprovedMessage
                : response.Message == "user did not respond within timeout"
                    ? _cosmetics.TimeoutMessage
                    : _cosmetics.DeniedMessage;
            _transcript.Append(line + "\n");
        }
        return response;
    }
}
