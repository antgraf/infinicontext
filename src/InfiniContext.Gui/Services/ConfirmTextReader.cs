namespace InfiniContext.Gui.Services;

/// <summary>
/// Bridges the Core task handler's confirmation contract to a GUI modal dialog.
///
/// <para><c>TaskCommandHandler</c>'s cost guard writes its "Continue? [y/N]" prompt to its output
/// writer and then calls <see cref="TextReader.ReadLineAsync(CancellationToken)"/> on its injected
/// reader to read the answer. In the console that reader is stdin; the GUI has no stdin, so before
/// task 0017 the GUI passed <c>null</c> and <c>/task review</c> (without <c>--yes</c>) bailed right
/// after resolving scope - the reported "command did nothing" symptom. This adapter instead invokes
/// <paramref name="confirm"/> (which shows a modal dialog on the UI thread) and maps the boolean
/// result to the "y"/"n" line the handler expects, so confirmation logic stays single-sourced in
/// Core rather than being re-implemented in the GUI.</para>
///
/// <para>Only <see cref="ReadLineAsync(CancellationToken)"/> is wired - it is the sole reader call
/// the handler makes. The base <see cref="TextReader"/> members keep their defaults (end-of-stream),
/// which read as a safe "no" should any future caller use them.</para>
/// </summary>
internal sealed class ConfirmTextReader : TextReader
{
    private readonly Func<CancellationToken, Task<bool>> _confirm;

    public ConfirmTextReader(Func<CancellationToken, Task<bool>> confirm)
        => _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var ok = await _confirm(cancellationToken).ConfigureAwait(false);
        return ok ? "y" : "n";
    }
}
