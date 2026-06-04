namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// The single registration site for host-handled slash-commands. Both UIs
/// (<c>InfiniContext.Cli</c>'s <c>MultiSessionHost</c> and <c>InfiniContext.Gui</c>'s
/// <c>MainWindowViewModel</c>) build their <see cref="SlashCommandRegistry"/> from
/// <see cref="Build"/> and nowhere else, so a command can never be registered in one UI but not
/// the other. Adding or removing a command is a one-line edit here; <c>RegistrySnapshotTests</c>
/// pins the resulting command set so a drift between this list and the snapshot fails loudly.
/// </summary>
public static class SlashCommandRegistryFactory
{
    public static SlashCommandRegistry Build() =>
        new(new ISlashCommand[]
        {
            new QuitCommand(),
            new CostCommand(),
            new StateCommand(),
            new HelpCommand(),
            new SettingsCommand(),
            new ResumeCommand(),
            new ApprovalCommand(),
            new TaskSlashCommand(),
            new IssueSlashCommand(),
            new FlushSlashCommand(),
            new NewSessionCommand(),
            new ListSessionsCommand(),
            new FocusCommand(),
            new FocusNextCommand(),
            new FocusPrevCommand(),
            new BroadcastCommand(),
        });
}
