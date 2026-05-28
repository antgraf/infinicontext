namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// One host-handled REPL slash-command. Each command self-describes via <see cref="Summary"/> +
/// <see cref="Details"/>, which the in-REPL <c>/help</c> renders directly. Adding a new command
/// to the <see cref="SlashCommandRegistry"/> automatically updates <c>/help</c>; there is no
/// other source of truth to keep aligned.
/// </summary>
public interface ISlashCommand
{
    /// <summary>Primary trigger including the leading slash, e.g. <c>"/help"</c>.</summary>
    string Name { get; }

    /// <summary>Aliases including the leading slash, e.g. <c>["/?"]</c>. May be empty.</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>One line. Shown in the <c>/help</c> summary table.</summary>
    string Summary { get; }

    /// <summary>Multi-line. Shown by <c>/help &lt;name&gt;</c>. May include usage examples.</summary>
    string Details { get; }

    /// <summary>
    /// Execute the command. <paramref name="argLine"/> is everything after the command name (or
    /// <c>""</c> if none). Exit / resume / switch are signalled via <see cref="IReplActions"/>.
    /// </summary>
    Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct);
}
