using Avalonia.Controls;

namespace InfiniContext.Gui.Views;

/// <summary>
/// Minimal modal yes/no confirmation. Used for the <c>/task review</c> cost guard (task 0017):
/// the GUI has no stdin reader, so the handler's "Continue? [y/N]" prompt is surfaced here instead.
/// <see cref="ShowAsync"/> returns <c>true</c> when the operator clicks Run, <c>false</c> on Cancel
/// or when the window is closed by other means (default <see cref="ShowDialog{TResult}"/> result).
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        RunButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }

    public ConfirmDialog(string message) : this() => MessageLabel.Text = message;

    /// <summary>Show modal over <paramref name="parent"/> and await the operator's choice.</summary>
    public Task<bool> ShowAsync(Window parent) => ShowDialog<bool>(parent);
}
