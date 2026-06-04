using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

public sealed class CostCommand : ISlashCommand
{
    public string Name => "/cost";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Show the most recent turn's cost in USD and turn count.";
    public string Details =>
        """
        /cost

        Prints the cost (in USD) and turn count from the most recent `result` event
        emitted by claude. Shows "no completed turns yet" before the first turn finishes.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        var cost = ctx.GetLastCostUsd();
        var turns = ctx.GetLastNumTurns();
        if (cost is { } c)
            ctx.Renderer.Write($"[cost] ${c:F6} ({turns?.ToString() ?? "?"} turns)\n");
        else
            ctx.Renderer.Write("[cost] no completed turns yet\n");
        return Task.CompletedTask;
    }
}
