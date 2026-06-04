using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Core.SlashCommands;

public sealed class SettingsCommand : ISlashCommand
{
    public string Name => "/settings";
    public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
    public string Summary => "Dump the effective layered settings as JSON.";
    public string Details =>
        """
        /settings

        Prints the layered effective settings (committed defaults from settings.json +
        per-user overrides from settings.user.json + INFINICONTEXT__* env-var overlays) as
        JSON. Useful for verifying which knobs are actually active in this run.
        """;

    public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct)
    {
        ctx.Renderer.Write(ctx.EffectiveSettingsJson ?? "[settings] no effective config available\n");
        ctx.Renderer.Write("\n");
        return Task.CompletedTask;
    }
}
