using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using InfiniContext.Core.SlashCommands;

namespace InfiniContext.Gui.Services;

/// <summary>
/// GUI implementation of <see cref="ISlashCommandRenderer"/>. Appends command output inline into
/// the focused session's transcript (the same surface the conversation renders), so slash-command
/// results show up where the operator is already looking. The <c>append</c> delegate is supplied
/// by the view-model and is responsible for marshalling to the UI thread.
///
/// <para>Interactive prompts (<see cref="PromptForChoiceAsync"/>/<see cref="PromptForConfirmAsync"/>)
/// are not yet backed by a modal dialog in the GUI and resolve to "cancelled"/"no". The current
/// command set does not call them on its happy paths (<c>/task review</c> confirmation flows
/// through the handler's <c>TextReader</c>, which the GUI leaves null in favour of <c>--yes</c>).</para>
/// </summary>
public sealed class GuiSlashCommandRenderer : ISlashCommandRenderer
{
    private static readonly Regex MarkupTag = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);
    private readonly Action<string> _append;

    public GuiSlashCommandRenderer(Action<string> append) =>
        _append = append ?? throw new ArgumentNullException(nameof(append));

    public void Write(string text) => _append(text);
    public void WriteLine(string text) => _append(text + "\n");
    public void WriteMarkup(string markup) => _append(StripMarkup(markup) + "\n");
    public void WriteError(string text) => _append(text + "\n");
    public void WriteSuccess(string text) => _append(text + "\n");
    public void WriteWarning(string text) => _append(text + "\n");

    public void WriteTable(string? title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title)) sb.Append(title).Append('\n');

        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++) widths[c] = headers[c].Length;
        foreach (var row in rows)
            for (var c = 0; c < headers.Count && c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c]?.Length ?? 0);

        AppendRow(sb, headers, widths);
        sb.Append(string.Join("  ", widths.Select(w => new string('-', w)))).Append('\n');
        foreach (var row in rows) AppendRow(sb, row, widths);
        _append(sb.ToString());
    }

    public Task<string?> PromptForChoiceAsync(string label, IReadOnlyList<string> options, CancellationToken ct)
    {
        _append($"{label} (choice prompts are not available in the GUI; cancelled)\n");
        return Task.FromResult<string?>(null);
    }

    public Task<bool> PromptForConfirmAsync(string label, CancellationToken ct)
    {
        _append($"{label} (confirmation prompts are not available in the GUI; treated as 'no' - use --yes)\n");
        return Task.FromResult(false);
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        for (var c = 0; c < widths.Length; c++)
        {
            var cell = c < cells.Count ? cells[c] ?? string.Empty : string.Empty;
            sb.Append(cell.PadRight(widths[c]));
            if (c < widths.Length - 1) sb.Append("  ");
        }
        sb.Append('\n');
    }

    private static string StripMarkup(string markup) => MarkupTag.Replace(markup, string.Empty);
}

/// <summary>
/// A <see cref="TextWriter"/> that forwards everything written to it into a transcript via the
/// supplied <c>append</c> delegate. The task/issue command handlers (promoted to Core in slice C)
/// write their output through a plain <see cref="TextWriter"/>; in the GUI we point that at the
/// focused session's transcript so handler output lands next to the command-class output.
/// </summary>
public sealed class TranscriptTextWriter : TextWriter
{
    private readonly Action<string> _append;
    public TranscriptTextWriter(Action<string> append) =>
        _append = append ?? throw new ArgumentNullException(nameof(append));

    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => _append(value.ToString());
    public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) _append(value); }
}
