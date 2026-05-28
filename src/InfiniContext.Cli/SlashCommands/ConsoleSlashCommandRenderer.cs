using InfiniContext.Core.SlashCommands;
using Spectre.Console;

namespace InfiniContext.Cli.SlashCommands;

/// <summary>
/// CLI-side <see cref="ISlashCommandRenderer"/> backed by a Spectre <see cref="IAnsiConsole"/>.
/// Wraps a host-supplied write lock so concurrent fragment-appended writes from the transcript
/// reader and slash-command writes don't interleave mid-line.
/// </summary>
internal sealed class ConsoleSlashCommandRenderer : ISlashCommandRenderer
{
    private readonly IAnsiConsole _console;
    private readonly object _writeLock;

    public ConsoleSlashCommandRenderer(IAnsiConsole console, object writeLock)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _writeLock = writeLock ?? throw new ArgumentNullException(nameof(writeLock));
    }

    public void Write(string text)
    {
        lock (_writeLock)
        {
            _console.Write(text);
        }
    }

    public void WriteLine(string text)
    {
        lock (_writeLock)
        {
            _console.Write(text);
            _console.Write("\n");
        }
    }

    public void WriteMarkup(string markup)
    {
        lock (_writeLock)
        {
            _console.Markup(markup);
        }
    }

    public void WriteError(string text)
    {
        lock (_writeLock)
        {
            _console.MarkupLine($"[red]{Markup.Escape(text)}[/]");
        }
    }

    public void WriteSuccess(string text)
    {
        lock (_writeLock)
        {
            _console.MarkupLine($"[green]{Markup.Escape(text)}[/]");
        }
    }

    public void WriteWarning(string text)
    {
        lock (_writeLock)
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(text)}[/]");
        }
    }

    public void WriteTable(string? title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        lock (_writeLock)
        {
            var table = new Table().Border(TableBorder.Rounded);
            if (!string.IsNullOrEmpty(title))
                table.Title = new TableTitle($"[bold]{Markup.Escape(title)}[/]");
            table.AddColumns(headers.Select(h => Markup.Escape(h)).ToArray());
            foreach (var row in rows)
            {
                table.AddRow(row.Select(cell => Markup.Escape(cell)).ToArray());
            }
            _console.Write(table);
        }
    }

    public Task<string?> PromptForChoiceAsync(string label, IReadOnlyList<string> options, CancellationToken ct)
    {
        if (options.Count == 0) return Task.FromResult<string?>(null);
        var prompt = new SelectionPrompt<string>().Title(label);
        foreach (var opt in options) prompt.AddChoice(opt);
        var chosen = _console.Prompt(prompt);
        return Task.FromResult<string?>(chosen);
    }

    public Task<bool> PromptForConfirmAsync(string label, CancellationToken ct)
    {
        var answer = _console.Confirm(label);
        return Task.FromResult(answer);
    }
}
