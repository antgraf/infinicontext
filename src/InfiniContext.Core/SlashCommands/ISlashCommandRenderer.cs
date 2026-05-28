namespace InfiniContext.Core.SlashCommands;

/// <summary>
/// The UI seam every slash-command writes through. The CLI implementation wraps Spectre.Console;
/// the GUI implementation appends into an in-window command-output panel. Commands depend only on
/// this abstraction so the same command class works in both UIs without recompilation.
///
/// <para>
/// Method shapes are deliberately conservative -- raw strings + simple table cells, no styled-text
/// objects. CLI consumers escape / format internally; GUI consumers may strip or ignore markup as
/// best fits their surface. The spec is "all the information from the CLI is visible to the GUI
/// operator", not "identical visuals".
/// </para>
/// </summary>
public interface ISlashCommandRenderer
{
    /// <summary>Append <paramref name="text"/> to the output surface verbatim. Does NOT append a
    /// trailing newline; commands that want one include it in <paramref name="text"/>.</summary>
    void Write(string text);

    /// <summary>Append <paramref name="text"/> followed by a single <c>\n</c>. Convenience for the
    /// common "one line then newline" pattern.</summary>
    void WriteLine(string text);

    /// <summary>Append a markup-formatted line. CLI consumers parse Spectre markup tags (e.g.
    /// <c>[bold]X[/]</c>); GUI consumers may strip unknown tags. Use only for output where the
    /// markup is part of the intent -- prefer <see cref="Write"/> for plain strings.</summary>
    void WriteMarkup(string markup);

    /// <summary>Render an error line. CLI implementations color it red; GUI implementations may
    /// use a distinct visual treatment. The leading <c>[command]</c> tag in <paramref name="text"/>
    /// is the caller's responsibility.</summary>
    void WriteError(string text);

    /// <summary>Render a success line. CLI implementations color it green.</summary>
    void WriteSuccess(string text);

    /// <summary>Render a warning line. CLI implementations color it yellow.</summary>
    void WriteWarning(string text);

    /// <summary>Render a tabular block. CLI implementations use a Spectre <c>Table</c>; GUI
    /// implementations use an Avalonia <c>Grid</c> inside the command-output panel. Cells are
    /// passed in their plain (unescaped) form -- the renderer handles any escaping.</summary>
    /// <param name="title">Optional title rendered above the table.</param>
    /// <param name="headers">One label per column.</param>
    /// <param name="rows">Each row has the same length as <paramref name="headers"/>.</param>
    void WriteTable(string? title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows);

    /// <summary>Prompt the operator to pick one of <paramref name="options"/>. Returns the chosen
    /// value, or <c>null</c> if cancelled. CLI implementations use a Spectre selection prompt;
    /// GUI implementations raise a modal dialog.</summary>
    Task<string?> PromptForChoiceAsync(string label, IReadOnlyList<string> options, CancellationToken ct);

    /// <summary>Prompt the operator for a yes/no answer. Returns <c>true</c> on yes, <c>false</c>
    /// on no or cancel.</summary>
    Task<bool> PromptForConfirmAsync(string label, CancellationToken ct);
}
