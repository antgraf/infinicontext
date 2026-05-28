namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// Immutable lookup of host-handled slash-commands. Resolves by <see cref="ISlashCommand.Name"/>
/// or any alias, case-insensitively. Sorted views drive the <c>/help</c> output.
///
/// The registry is the single source of truth for "what commands exist" and "what they do" --
/// adding a new command lights up <c>/help</c> automatically; no parallel doc/list to update.
/// </summary>
public sealed class SlashCommandRegistry
{
    private readonly IReadOnlyList<ISlashCommand> _commands;
    private readonly Dictionary<string, ISlashCommand> _byTrigger;

    public SlashCommandRegistry(IEnumerable<ISlashCommand> commands)
    {
        _commands = commands.ToArray();
        _byTrigger = new Dictionary<string, ISlashCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _commands)
        {
            Add(c.Name, c);
            foreach (var a in c.Aliases) Add(a, c);
        }

        void Add(string trigger, ISlashCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(trigger) || trigger[0] != '/')
                throw new ArgumentException(
                    $"Slash-command trigger must start with '/'. Got: '{trigger}' on {cmd.GetType().Name}.");
            if (!_byTrigger.TryAdd(trigger, cmd))
                throw new ArgumentException(
                    $"Duplicate slash-command trigger '{trigger}' on {cmd.GetType().Name} and {_byTrigger[trigger].GetType().Name}.");
        }
    }

    /// <summary>All registered commands, in registration order.</summary>
    public IReadOnlyList<ISlashCommand> Commands => _commands;

    /// <summary>Resolve a trigger (with leading slash) to a command, or null if unknown.</summary>
    public ISlashCommand? TryResolve(string nameWithSlash) =>
        _byTrigger.TryGetValue(nameWithSlash, out var cmd) ? cmd : null;

    /// <summary>Commands sorted alphabetically by Name -- what <c>/help</c> renders.</summary>
    public IReadOnlyList<ISlashCommand> SortedForHelp() =>
        _commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}
