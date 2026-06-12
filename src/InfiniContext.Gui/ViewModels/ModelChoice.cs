namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// One selectable entry in a model-picker ComboBox (new-task dialog, per-session inspector).
/// <see cref="Id"/> is the claude model id passed to a spawn, or <c>null</c> for the synthetic
/// "host default" choice — which leaves the model unset so spawn resolution falls through to
/// <c>settings.Session.Model</c> / <c>settings.Models.Main.Id</c>. <see cref="Label"/> is the
/// human-readable text shown in the dropdown.
/// </summary>
public sealed record ModelChoice(string? Id, string Label);
