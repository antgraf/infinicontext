namespace InfiniContext.Core.Tasks;

/// <summary>
/// Result of <see cref="TaskService.PrepareSwitchAsync"/>: everything the host needs to spawn
/// a fresh session bound to <see cref="Task"/>. The host passes <see cref="FirstUserMessage"/>
/// through <c>ClaudeSessionOptions.FirstUserMessage</c> so the session writes it to stdin
/// after the first <c>system/init</c> event.
/// </summary>
/// <param name="Task">The selected task row at the moment of switch.</param>
/// <param name="Slug">Slug recovered from the title via <see cref="Slugify.For"/>.</param>
/// <param name="FirstUserMessage">Pointer string to send into the new session.</param>
/// <param name="Drift">Files on disk under the task folder that aren't referenced from <c>index.md</c>.</param>
/// <param name="MissingFiles">Index rows pointing at files that no longer exist on disk (M17).</param>
public sealed record TaskSwitchPlan(
    TaskRecord Task,
    string Slug,
    string FirstUserMessage,
    IReadOnlyList<string> Drift,
    IReadOnlyList<string>? MissingFiles = null);
