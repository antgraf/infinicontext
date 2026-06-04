using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

public sealed class QuitCommand : ISlashCommand
{
    public string Name => "/quit";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/exit" };
    public string Summary => "Exit the REPL (cleanly closes the spawned claude session).";
    public string Details =>
        """
        /quit
        /exit  (alias)

        Exits the InfiniContext REPL. The spawned claude child is signalled to shut down
        cleanly; any in-flight turn is cancelled first.

        Tip: pressing Ctrl+C while the REPL is idle has the same effect.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        ctx.Actions.RequestExit();
        return Task.CompletedTask;
    }
}
