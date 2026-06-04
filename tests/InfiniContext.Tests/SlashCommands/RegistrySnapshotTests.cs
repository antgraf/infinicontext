using FluentAssertions;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Tests.SlashCommands;

/// <summary>
/// Drift guard for the single slash-command registration site (task 0015). Both the CLI host and
/// the GUI build their registry from <see cref="SlashCommandRegistryFactory.Build"/>; this test
/// pins the resulting command set so adding a command to the factory without updating the snapshot
/// (or vice-versa) fails loudly. Updating <see cref="ExpectedNames"/> IS the intended way to opt a
/// new command into both UIs.
/// </summary>
public class RegistrySnapshotTests
{
    private static readonly string[] ExpectedNames =
    {
        "/approval",
        "/broadcast",
        "/cost",
        "/flush",
        "/focus",
        "/help",
        "/issue",
        "/list",
        "/n",
        "/next",
        "/prev",
        "/quit",
        "/resume",
        "/settings",
        "/state",
        "/task",
    };

    [Fact]
    public void Factory_registers_exactly_the_snapshotted_command_set()
    {
        var registry = SlashCommandRegistryFactory.Build();

        registry.Commands
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Should()
            .Equal(ExpectedNames,
                "the factory is the single registration site for both CLI and GUI - update " +
                "ExpectedNames deliberately when opting a command in or out");
    }

    [Fact]
    public void Every_command_name_and_alias_resolves()
    {
        var registry = SlashCommandRegistryFactory.Build();

        foreach (var cmd in registry.Commands)
        {
            registry.TryResolve(cmd.Name).Should().BeSameAs(cmd, $"name {cmd.Name} must resolve");
            foreach (var alias in cmd.Aliases)
                registry.TryResolve(alias).Should().BeSameAs(cmd, $"alias {alias} must resolve to {cmd.Name}");
        }
    }
}
