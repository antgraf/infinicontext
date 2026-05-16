using System.Diagnostics;
using InfiniContext.Core.Mcp;

namespace InfiniContext.Cli;

/// <summary>
/// Decorator that emits a one-line summary of every approval decision after the inner sink
/// resolves. Logs are produced centrally by <see cref="Core.Mcp.ApprovalChannel"/>; this decorator
/// covers the user-visible side so <c>deny</c>/<c>allow</c>/<c>auto</c> modes (which don't render
/// their own banner) still print what was decided.
///
/// <para>
/// The output target is an <see cref="Action{T}"/> over a string. Stage 2B Phase 5 uses this to
/// route the decision line into the originating session's <see cref="Sessions.TranscriptModel"/>
/// (via <c>transcript.Append</c>) so the line lands in the right pane in a multi-session UI. The
/// <see cref="TextWriter"/> ctor remains for tests and any single-stream caller.
/// </para>
///
/// <para>Format:</para>
/// <list type="bullet">
///   <item>Allow: <c>[approval] &lt;Tool&gt; → ALLOW (Nms)</c></item>
///   <item>Deny:  <c>[approval] &lt;Tool&gt; → DENY (Nms): &lt;reason&gt;</c></item>
/// </list>
/// </summary>
internal sealed class ConsoleEchoingApprovalSink : IApprovalSink
{
    private readonly IApprovalSink _inner;
    private readonly Action<string> _emit;

    public ConsoleEchoingApprovalSink(IApprovalSink inner, Action<string> emit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    public ConsoleEchoingApprovalSink(IApprovalSink inner, TextWriter output, object writeLock)
        : this(inner, BuildLockingWriter(output, writeLock)) { }

    public IApprovalSink Inner => _inner;

    public async Task<ApprovalResponse> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        ApprovalResponse response;
        try { response = await _inner.RequestAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { sw.Stop(); }

        var line = response.Behavior == ApprovalBehavior.Allow
            ? $"[approval] {request.ToolName} → ALLOW ({sw.ElapsedMilliseconds}ms)"
            : $"[approval] {request.ToolName} → DENY ({sw.ElapsedMilliseconds}ms): {response.Message ?? "(no reason)"}";
        _emit(line);
        return response;
    }

    private static Action<string> BuildLockingWriter(TextWriter output, object writeLock)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writeLock);
        return line =>
        {
            lock (writeLock)
            {
                output.WriteLine();
                output.WriteLine(line);
                output.Flush();
            }
        };
    }
}
