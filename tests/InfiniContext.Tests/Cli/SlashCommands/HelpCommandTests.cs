using InfiniContext.Cli.SlashCommands;
using InfiniContext.Cli.Tasks;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Events;
using InfiniContext.Core.Sessions;
using InfiniContext.Core.State;
using InfiniContext.Core.Tasks;
using FluentAssertions;
using System.Threading.Channels;

namespace InfiniContext.Tests.Cli.SlashCommands;

/// <summary>
/// Tests <see cref="HelpCommand"/> output against a fully-populated registry. The contract is:
/// /help (no arg) lists every registered command's <c>Summary</c>; /help &lt;topic&gt; prints
/// the command's <c>Details</c>; /help &lt;unknown-topic&gt; falls back to the summary plus a
/// "no such topic" line (decisions.md D9).
/// </summary>
public class HelpCommandTests
{
    [Fact]
    public async Task NoArg_lists_every_registered_command_with_its_summary()
    {
        var (registry, output, ctx) = MakeContext();

        var help = registry.TryResolve("/help")!;
        await help.ExecuteAsync(ctx, "", CancellationToken.None);

        var text = output.ToString();
        // Header line.
        text.Should().Contain("REPL commands");

        // Every registered command's name AND its summary appear in the output.
        foreach (var cmd in registry.Commands)
        {
            text.Should().Contain(cmd.Name);
            text.Should().Contain(cmd.Summary);
        }
    }

    [Fact]
    public async Task NoArg_mentions_the_fall_through_to_claude()
    {
        var (registry, output, ctx) = MakeContext();

        await registry.TryResolve("/help")!.ExecuteAsync(ctx, "", CancellationToken.None);

        // Users need to know that unrecognised /commands reach claude — that's a behavioural
        // contract, not a fun fact, and /help is the natural place to surface it.
        output.ToString().Should().Contain("forwarded to claude");
    }

    [Theory]
    [InlineData("task")]
    [InlineData("/task")]
    [InlineData("approval")]
    [InlineData("/approval")]
    [InlineData("quit")]
    [InlineData("exit")]      // alias of /quit
    [InlineData("?")]         // alias of /help
    public async Task TopicArg_renders_the_full_details_for_that_command(string topic)
    {
        var (registry, output, ctx) = MakeContext();
        var resolved = HelpCommand.ResolveTopic(registry, topic);
        resolved.Should().NotBeNull($"topic '{topic}' must resolve to a registered command");

        await registry.TryResolve("/help")!.ExecuteAsync(ctx, topic, CancellationToken.None);

        output.ToString().TrimEnd().Should().Be(resolved!.Details.TrimEnd());
    }

    [Fact]
    public async Task UnknownTopic_falls_back_to_summary_with_no_such_topic_line()
    {
        var (registry, output, ctx) = MakeContext();

        await registry.TryResolve("/help")!.ExecuteAsync(ctx, "nope", CancellationToken.None);

        var text = output.ToString();
        text.Should().Contain("REPL commands");                       // summary
        text.Should().Contain("[help] no such topic 'nope'");         // diagnostic
    }

    [Fact]
    public async Task Topic_is_case_insensitive()
    {
        var (registry, output, ctx) = MakeContext();

        await registry.TryResolve("/help")!.ExecuteAsync(ctx, "TASK", CancellationToken.None);

        var taskCmd = registry.TryResolve("/task")!;
        output.ToString().TrimEnd().Should().Be(taskCmd.Details.TrimEnd());
    }

    [Fact]
    public void RenderSummary_lists_commands_in_alphabetical_order()
    {
        var (registry, _, _) = MakeContext();

        var summary = HelpCommand.RenderSummary(registry);
        // Pull the table rows (lines starting with two-space indent then a slash) and read the
        // command name out of each. This avoids false hits on /help in the header text.
        var rendered = summary
            .Split('\n')
            .Where(line => line.StartsWith("  /", StringComparison.Ordinal))
            .Select(line => line.TrimStart().Split(' ', 2)[0])
            .ToArray();
        var expected = registry.SortedForHelp().Select(c => c.Name).ToArray();

        rendered.Should().Equal(expected);
    }

    private static (SlashCommandRegistry registry, StringWriter output, SlashCommandContext ctx) MakeContext()
    {
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new QuitCommand(),
            new CostCommand(),
            new StateCommand(),
            new HelpCommand(),
            new SettingsCommand(),
            new ResumeCommand(),
            new ApprovalCommand(),
            new TaskSlashCommand(),
        });

        var output = new StringWriter();
        var session = new StubSession();
        var ctx = new SlashCommandContext(
            Session: session,
            Write: s => output.Write(s),
            Settings: InfiniContextSettings.Defaults,
            EffectiveSettingsJson: "{}",
            GetApprovalMode: null,
            SwitchApprovalMode: null,
            TaskHandler: null,
            PrepareTaskSwitch: null,
            GetLastCostUsd: () => null,
            GetLastNumTurns: () => null,
            Actions: new NoopActions(),
            Registry: registry);
        return (registry, output, ctx);
    }

    private sealed class NoopActions : IReplActions
    {
        public void RequestExit() { }
        public void RequestResume(string sessionId) { }
        public void RequestSwitch(TaskSwitchPlan plan) { }
        public void RequestFocus(Guid hostSessionId) { }
    }

    private sealed class StubSession : IClaudeSession
    {
        private readonly Channel<ClaudeEvent> _events = Channel.CreateUnbounded<ClaudeEvent>();
        public Guid? SessionId => null;
        public SessionStateMachine State { get; } = new();
        public ChannelReader<ClaudeEvent> Events => _events.Reader;
        public IReadOnlyList<string> StderrTail { get; } = Array.Empty<string>();
        public int UnknownEventCount => 0;
        public int MalformedLineCount => 0;
        public IReadOnlyDictionary<string, int> UnknownTypeHistogram { get; } = new Dictionary<string, int>();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
