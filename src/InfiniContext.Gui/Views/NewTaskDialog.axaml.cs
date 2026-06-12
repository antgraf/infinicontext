using Avalonia.Controls;
using Avalonia.Platform.Storage;
using InfiniContext.Gui.ViewModels;

namespace InfiniContext.Gui.Views;

/// <summary>
/// Modal dialog for the "+ New task" button (task 0012 finding #5). Captures a home folder
/// and a description; the parent view-model takes the result and calls
/// <c>TaskService.CreateAsync</c> which derives the title via the light model.
///
/// <para>
/// Returns <see cref="NewTaskRequest"/> when the operator confirms, or <c>null</c> if they
/// cancel / close the window. The dialog itself does no I/O - all work happens after the
/// caller awaits <see cref="ShowAsync"/>.
/// </para>
/// </summary>
public partial class NewTaskDialog : Window
{
    private NewTaskRequest? _result;

    public NewTaskDialog()
    {
        InitializeComponent();
        CreateButton.Click += OnCreate;
        CancelButton.Click += OnCancel;
        BrowseButton.Click += OnBrowse;
    }

    /// <summary>Populate the model dropdown. Call before <see cref="ShowAsync"/>. The first
    /// entry (typically the "host default" sentinel with a null id) is selected by default so a
    /// task created without touching the dropdown pins no per-task model.</summary>
    public void SetModelChoices(IReadOnlyList<ModelChoice> choices)
    {
        ModelBox.ItemsSource = choices;
        if (choices.Count > 0) ModelBox.SelectedIndex = 0;
    }

    /// <summary>Show modal over <paramref name="parent"/> and await the operator's choice.</summary>
    public Task<NewTaskRequest?> ShowAsync(Window parent) => ShowDialog<NewTaskRequest?>(parent);

    private async void OnBrowse(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picker = StorageProvider;
        if (picker is null) return;
        var folders = await picker.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Pick a home folder for the new task",
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) HomeCwdBox.Text = path;
    }

    private void OnCreate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var home = HomeCwdBox.Text?.Trim();
        var desc = DescriptionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(home))
        {
            ShowError("Pick a home folder before creating the task.");
            return;
        }
        if (string.IsNullOrWhiteSpace(desc))
        {
            ShowError("Description cannot be empty - it becomes the task's description.md.");
            return;
        }
        var model = (ModelBox.SelectedItem as ModelChoice)?.Id;
        _result = new NewTaskRequest(home, desc, model);
        Close(_result);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _result = null;
        Close(null);
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }
}

/// <summary>Operator-confirmed inputs from <see cref="NewTaskDialog"/>.</summary>
/// <param name="HomeCwd">Home folder for the task. Must exist and be writable.</param>
/// <param name="Description">Free-form description that becomes <c>description.md</c>.</param>
/// <param name="Model">Chosen per-task default model id, or <c>null</c> for the host default
/// (no per-task pin вЂ” spawn falls back to the settings model chain).</param>
public sealed record NewTaskRequest(string HomeCwd, string Description, string? Model = null);
