using InfiniContext.Cli.SlashCommands;
using InfiniContext.Core.SlashCommands;
using FluentAssertions;

namespace InfiniContext.Tests.Cli.SlashCommands;

/// <summary>
/// Pure-logic tests for <see cref="SlashCommandRegistry"/>. Confirms the registry rejects
/// duplicate triggers, treats name + alias resolution as case-insensitive, and produces a
/// deterministic sort for /help.
/// </summary>
public class SlashCommandRegistryTests
{
    [Fact]
    public void TryResolve_finds_command_by_primary_name()
    {
        var reg = new SlashCommandRegistry(new ISlashCommand[] { new QuitCommand(), new HelpCommand() });

        reg.TryResolve("/quit").Should().BeOfType<QuitCommand>();
        reg.TryResolve("/help").Should().BeOfType<HelpCommand>();
    }

    [Fact]
    public void TryResolve_finds_command_by_alias()
    {
        var reg = new SlashCommandRegistry(new ISlashCommand[] { new QuitCommand(), new HelpCommand() });

        reg.TryResolve("/exit").Should().BeOfType<QuitCommand>();
        reg.TryResolve("/?").Should().BeOfType<HelpCommand>();
    }

    [Fact]
    public void TryResolve_is_case_insensitive()
    {
        var reg = new SlashCommandRegistry(new ISlashCommand[] { new QuitCommand() });

        reg.TryResolve("/QUIT").Should().BeOfType<QuitCommand>();
        reg.TryResolve("/Quit").Should().BeOfType<QuitCommand>();
        reg.TryResolve("/eXiT").Should().BeOfType<QuitCommand>();
    }

    [Fact]
    public void TryResolve_returns_null_for_unknown()
    {
        var reg = new SlashCommandRegistry(new ISlashCommand[] { new QuitCommand() });

        reg.TryResolve("/clear").Should().BeNull();
        reg.TryResolve("/whatever").Should().BeNull();
    }

    [Fact]
    public void Constructor_throws_on_duplicate_trigger()
    {
        // Two commands both registering "/quit" (one as Name, one as Alias) is a wiring bug
        // we want surfaced loudly, not silently absorbed.
        var dup = new DuplicateTriggerCommand();
        var act = () => new SlashCommandRegistry(new ISlashCommand[] { new QuitCommand(), dup });

        act.Should().Throw<ArgumentException>().WithMessage("*Duplicate*'/quit'*");
    }

    [Fact]
    public void Constructor_throws_when_trigger_does_not_start_with_slash()
    {
        var bad = new BadTriggerCommand();
        var act = () => new SlashCommandRegistry(new ISlashCommand[] { bad });

        act.Should().Throw<ArgumentException>().WithMessage("*must start with '/'*");
    }

    [Fact]
    public void SortedForHelp_returns_commands_in_alphabetical_order()
    {
        var reg = new SlashCommandRegistry(new ISlashCommand[]
        {
            new TaskSlashCommand(),  // /task
            new HelpCommand(),       // /help
            new QuitCommand(),       // /quit
        });

        var names = reg.SortedForHelp().Select(c => c.Name).ToArray();

        names.Should().Equal("/help", "/quit", "/task");
    }

    private sealed class DuplicateTriggerCommand : ISlashCommand
    {
        public string Name => "/quit"; // collides with QuitCommand.Name
        public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
        public string Summary => "dup";
        public string Details => "dup";
        public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class BadTriggerCommand : ISlashCommand
    {
        public string Name => "no-slash";
        public IReadOnlyList<string> Aliases { get; } = Array.Empty<string>();
        public string Summary => "bad";
        public string Details => "bad";
        public Task ExecuteAsync(SlashCommandContext ctx, string argLine, CancellationToken ct) => Task.CompletedTask;
    }
}
