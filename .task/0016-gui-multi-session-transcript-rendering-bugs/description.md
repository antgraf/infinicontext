# GUI multi-session transcript rendering bugs

Multiple sessions in a task still has major issues.

Steps to reproduce:

1. Launch GUI application (multiple tasks already exist and are shown in the left panel).
2. Click "new session" action button for a task; wait until warmup is done.
3. Click "new session" action button one more time to spawn a second session.

Issues observed:

- Selection is set to the first idle session when a new one is created. However the text render is corrupted (transcript text fragments scattered across the pane — see `new-session-wrong-render.png` in the original report).
- Switching sessions reveals the correct text for the first session but empty for the second.
- Switching multiple times leads to an unhandled exception:

```
System.InvalidCastException
  Message=Unable to cast object of type 'Avalonia.Media.Color' to type 'Avalonia.Media.IBrush'.
   at Avalonia.PropertyStore.EffectiveValue`1.SetAndRaise(...)
   ...
   at Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExpression.ActualThemeVariantChanged(Object sender, EventArgs e)
   at ColorTextBlock.Avalonia.CInline.OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   at ColorTextBlock.Avalonia.CSpan.OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   ...
   at Avalonia.Controls.ContentControl.set_Content(Object value)
   at InfiniContext.Gui.Views.MainWindow.RebuildTranscript(SessionRowViewModel row) in src\InfiniContext.Gui\Views\MainWindow.axaml.cs:line 169
   at InfiniContext.Gui.Views.MainWindow.WatchSession(SessionRowViewModel row) in src\InfiniContext.Gui\Views\MainWindow.axaml.cs:line 145
   at InfiniContext.Gui.Views.MainWindow.OnVmPropertyChanged(...) in src\InfiniContext.Gui\Views\MainWindow.axaml.cs:line 120
   at InfiniContext.Gui.ViewModels.MainWindowViewModel.set_FocusedSession(...)
   at InfiniContext.Gui.ViewModels.MainWindowViewModel.OnSelectedNodeChanged(...) MainWindowViewModel.cs:line 316
   at InfiniContext.Gui.ViewModels.MainWindowViewModel.set_SelectedNode(...)
```

The crash fires inside `RebuildTranscript`, which replaces `TranscriptHost.Content` with a fresh `ItemsControl`:

```csharp
TranscriptHost.Content = new ItemsControl
{
    ItemsSource = row.Turns,
    ItemTemplate = (IDataTemplate)Resources["TurnNodeTemplate"]!,
    HorizontalAlignment = HorizontalAlignment.Stretch,
};
```

The cast failure originates in `ColorTextBlock.Avalonia` (`CInline`/`CSpan`, used by Markdown.Avalonia) re-evaluating a `DynamicResource` (theme variant) while the old content's logical tree is being detached — a `Color` resource lands where an `IBrush` is expected.
