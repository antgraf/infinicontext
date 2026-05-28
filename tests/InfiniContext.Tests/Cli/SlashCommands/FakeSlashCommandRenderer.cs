using System.Text;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Tests.Cli.SlashCommands;

/// <summary>
/// Test double for <see cref="ISlashCommandRenderer"/>. Captures every written fragment in a
/// <see cref="StringBuilder"/> so assertions can read the full output as one string. Tables are
/// rendered as a simple <c>title \n header1 | header2 \n row1cell1 | row1cell2 \n ...</c> shape --
/// enough fidelity for "did this string appear" assertions without pulling Spectre into the test
/// assembly.
/// </summary>
internal sealed class FakeSlashCommandRenderer : ISlashCommandRenderer
{
    private readonly StringBuilder _sb = new();

    public string Captured => _sb.ToString();

    public override string ToString() => _sb.ToString();

    public void Write(string text) => _sb.Append(text);

    public void WriteLine(string text)
    {
        _sb.Append(text);
        _sb.Append('\n');
    }

    public void WriteMarkup(string markup) => _sb.Append(markup);

    public void WriteError(string text)
    {
        _sb.Append(text);
        _sb.Append('\n');
    }

    public void WriteSuccess(string text)
    {
        _sb.Append(text);
        _sb.Append('\n');
    }

    public void WriteWarning(string text)
    {
        _sb.Append(text);
        _sb.Append('\n');
    }

    public void WriteTable(string? title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (!string.IsNullOrEmpty(title))
        {
            _sb.Append(title);
            _sb.Append('\n');
        }
        _sb.Append(string.Join(" | ", headers));
        _sb.Append('\n');
        foreach (var row in rows)
        {
            _sb.Append(string.Join(" | ", row));
            _sb.Append('\n');
        }
    }

    public Task<string?> PromptForChoiceAsync(string label, IReadOnlyList<string> options, CancellationToken ct)
        => throw new NotSupportedException("Tests must not exercise interactive prompts.");

    public Task<bool> PromptForConfirmAsync(string label, CancellationToken ct)
        => throw new NotSupportedException("Tests must not exercise interactive prompts.");
}
