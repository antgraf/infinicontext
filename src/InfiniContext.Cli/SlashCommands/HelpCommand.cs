using System.Text;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// Renders the in-REPL help text from <see cref="SlashCommandRegistry"/>. With no arg, prints a
/// one-screen summary table listing every registered command. With <c>/help &lt;topic&gt;</c>,
/// prints the resolved command's <see cref="ISlashCommand.Details"/>. Unknown topics fall back
/// to the summary plus a "no such topic" line (decisions.md D9).
/// </summary>
internal sealed class HelpCommand : ISlashCommand
{
    public string Name => "/help";
    public IReadOnlyList<string> Aliases { get; } = new[] { "/?" };
    public string Summary => "Show this help. /help <topic> for details on one command.";
    public string Details =>
        """
        /help            list every host-handled REPL command with a one-line summary
        /help <topic>    show full details for one command (e.g. /help task, /help approval)

        Topics are command names with or without the leading slash. Aliases work too,
        so /help exit and /help quit both reach the QuitCommand.

        Unknown commands at the prompt (anything not in the list below) are forwarded
        verbatim to the spawned claude — its own slash-commands like /clear or /compact
        will reach claude as if you'd typed them in a TTY session.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        var topic = argLine.Trim();
        if (topic.Length == 0)
        {
            ctx.Write(RenderSummary(ctx.Registry));
            return Task.CompletedTask;
        }

        var resolved = ResolveTopic(ctx.Registry, topic);
        if (resolved is null)
        {
            ctx.Write(RenderSummary(ctx.Registry));
            ctx.Write($"[help] no such topic '{topic}'\n");
            return Task.CompletedTask;
        }

        ctx.Write(resolved.Details.TrimEnd() + "\n");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve a topic argument to a command. Accepts "/help", "help", or any alias with or
    /// without a leading slash. Case-insensitive.
    /// </summary>
    internal static ISlashCommand? ResolveTopic(SlashCommandRegistry registry, string topic)
    {
        var normalised = topic.StartsWith('/') ? topic : "/" + topic;
        return registry.TryResolve(normalised);
    }

    /// <summary>Render the summary table. Public-by-name for tests; behavior is pure.</summary>
    internal static string RenderSummary(SlashCommandRegistry registry)
    {
        var sb = new StringBuilder();
        sb.Append("REPL commands (type /help <topic> for details):\n");
        var commands = registry.SortedForHelp();
        var nameWidth = commands.Max(c => c.Name.Length);
        foreach (var c in commands)
        {
            sb.Append("  ");
            sb.Append(c.Name.PadRight(nameWidth));
            sb.Append("  ");
            sb.Append(c.Summary);
            sb.Append('\n');
        }
        sb.Append("\nUnknown /commands are forwarded to claude verbatim (e.g. /clear, /compact).\n");
        return sb.ToString();
    }
}
