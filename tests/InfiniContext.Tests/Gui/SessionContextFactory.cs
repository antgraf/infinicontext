using System.Text.Json.Nodes;
using InfiniContext.Core.Events;
using InfiniContext.Core.Mcp;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Tests.Gui;

/// <summary>Builder for a <see cref="SessionContext"/> backed by a <see cref="FakeClaudeSession"/>.
/// Exposes the helpers the test matrix needs: drive raw events via <see cref="RaiseEvent"/>;
/// open/clear the approval slot; set LastNumTurns. Each instance creates a fresh
/// <see cref="SessionContext"/> with real Core deps (no Core mocking) - the only fake is the
/// IClaudeSession boundary.</summary>
internal sealed class SessionContextFactory
{
    public FakeClaudeSession Session { get; } = new();
    public SessionContext Context { get; }
    public PendingApprovalSlot ApprovalSlot { get; }

    public SessionContextFactory(int? activeTaskId = null, string? cwd = null)
    {
        var pressure = new ContextPressureMonitor(softThresholdPct: 75, hardThresholdPct: 85);
        var injector = new ThresholdInjector((_, __) => Task.CompletedTask);
        var slotSink = new SlotApprovalSink(new PendingApprovalSlot(), new TranscriptModel(), TimeSpan.FromSeconds(60));
        var approvalSink = new SwitchableApprovalSink(slotSink);
        ApprovalSlot = new PendingApprovalSlot();
        var transcript = new TranscriptModel();
        Context = new SessionContext(
            id: Guid.NewGuid(),
            session: Session,
            pressureMonitor: pressure,
            injector: injector,
            approvalSink: approvalSink,
            approvalSlot: ApprovalSlot,
            transcript: transcript,
            activeTaskId: activeTaskId)
        {
            Cwd = cwd,
        };
    }

    /// <summary>Drive a <see cref="ClaudeEvent"/> through the same path production takes: apply
    /// to the state machine first (mirrors <c>ClaudeSession.StdoutLoopAsync</c>'s ordering), then
    /// raise it on <see cref="SessionContext"/>. Tests use this in place of the real
    /// <see cref="InfiniContext.Core.Sessions.SessionTranscriptReader"/>.</summary>
    public void RaiseEvent(ClaudeEvent ev)
    {
        Session.State.Apply(ev);
        Context.RaiseEventObserved(ev);
    }

    public static SystemInitEvent MakeInit(string model = "claude-sonnet-4-6", string cwd = "/tmp") =>
        new()
        {
            Type = "system",
            Subtype = "init",
            SessionId = Guid.NewGuid(),
            Raw = new JsonObject(),
            Model = model,
            Cwd = cwd,
            ClaudeCodeVersion = "2.1.x",
            PermissionMode = "default",
        };

    public static ResultEvent MakeResult(int numTurns = 1, decimal costUsd = 0.0001m) =>
        new()
        {
            Type = "result",
            Subtype = "success",
            Raw = new JsonObject(),
            NumTurns = numTurns,
            TotalCostUsd = costUsd,
            DurationMs = 100,
            Result = "ack",
        };

    public static AssistantEvent MakeAssistantText(string text = "ok") =>
        new()
        {
            Type = "assistant",
            Raw = new JsonObject(),
            Content = new[]
            {
                new TextBlock { Type = "text", Text = text, Raw = new JsonObject() } as ContentBlock,
            },
        };

    /// <summary>Apply a synthetic LastNumTurns - simulates the <see cref="SessionManager"/> reader
    /// hook updating the context from a <see cref="ResultEvent"/> before raising EventObserved.</summary>
    public void SetLastNumTurns(int? value) => Context.LastNumTurns = value;
}
