# Design: shared slash-command surface (CLI + GUI)

**Scope:** finding #14 вЂ” `/`-prefix commands work in CLI but not GUI. Requirement from operator: design must make it impossible to register a command in one project but not the other.

## Existing CLI shape (good news вЂ” already half-done)

`InfiniContext.Cli/SlashCommands/` already factors slash-commands cleanly:

- `ISlashCommand` (internal interface): `Name`, `Aliases`, `Summary`, `Details`, `ExecuteAsync(SlashCommandContext, argLine, ct)`.
- `SlashCommandRegistry`: immutable lookup; one constructor takes `IEnumerable<ISlashCommand>` and validates uniqueness. Triggers must start with `/`. Duplicates throw. `TryResolve` does the lookup; `SortedForHelp` drives `/help`.
- `SlashCommandContext`: positional record holding `IClaudeSession`, `Action<string> Write`, `InfiniContextSettings`, `IAnsiConsole?`, `IReplActions`, `SessionManager?`, plus delegate callbacks for approval-mode get/switch, task-switch prep, cost/turn snapshots. Constructed once by the host; re-passed per-call.
- `IReplActions`: `RequestExit`, `RequestResume(sessionId)`, `RequestSwitch(TaskSwitchPlan)`, `RequestFocus(hostSessionId)`. Implemented by `MultiSessionHost`; stubbable for tests.
- Commands live as individual classes under `InfiniContext.Cli/SlashCommands/` and `InfiniContext.Cli/{Issues,Tasks}/` (e.g. `HelpCommand`, `NewSessionCommand`, `FocusCommand`, `ApprovalCommand`, `TaskSlashCommand`, `IssueSlashCommand`, `FlushSlashCommand`, вЂ¦).

**What this means:** the framework is sound. The work is (a) move it to Core, (b) abstract the few Spectre/Console-flavoured bits behind interfaces, (c) ensure the singular registration site.

## The five UI-coupled surfaces to abstract

Each is currently CLI-specific; each needs an interface so the GUI can implement its own.

1. **Plain text out.** `Action<string> Write` в†’ `ISlashCommandRenderer.WriteLine(string)`.
2. **Rich rendering.** `IAnsiConsole?` (Spectre tables, panels, markup) в†’ `ISlashCommandRenderer.WriteTable(headers, rows)`, `WriteMarkup(text)`, `WriteError(text)`, `WriteSuccess(text)`, `WriteWarning(text)`. CLI implements with Spectre; GUI implements by appending into a new `CommandOutputLog` ObservableCollection rendered in a popup/flyout (or a transcript pane variant for the focused session). For colour markup, the GUI ignores Spectre tags or maps a curated subset (`[red]` в†’ `Brushes.Red`, etc.); spec is "best-effort", not "pixel-identical".
3. **REPL lifecycle actions.** `IReplActions` stays as the abstraction. CLI's `RequestExit` ends the REPL loop; GUI's closes the main window. `RequestResume` / `RequestSwitch` / `RequestFocus` map to GUI VM commands (focus tree, swap row, etc.).
4. **Prompt for input (rare; `/resume` may list and pick).** Add `ISlashCommandRenderer.PromptForChoiceAsync(label, options, ct)` returning the chosen value or null on cancel. CLI uses `AnsiConsole.Prompt`; GUI raises a modal `ChoiceDialog`.
5. **Interactive confirms.** Same shape: `PromptForConfirmAsync(label, ct)` returning bool.

Renderer + actions are the entire UI seam. Everything else in `SlashCommandContext` (`IClaudeSession`, `InfiniContextSettings`, `SessionManager`, the delegate callbacks) is already UI-agnostic.

## Where the code moves

**New: `src/InfiniContext.Core/SlashCommands/`** (`Core.SlashCommands` namespace)
- `ISlashCommand` (promoted from internal to public)
- `SlashCommandRegistry` (promoted)
- `SlashCommandContext` (promoted; renderer field replaces `Write` + `Console`)
- `IReplActions` (promoted)
- `ISlashCommandRenderer` (new вЂ” the abstraction above)
- `SlashCommandRegistryFactory` (new вЂ” the **single registration site**, see below)
- All command classes themselves: `HelpCommand`, `NewSessionCommand`, `ListSessionsCommand`, `FocusCommand`, `BroadcastCommand`, `ResumeCommand`, `ApprovalCommand`, `SettingsCommand`, `StateCommand`, `CostCommand`, `QuitCommand`, `FlushSlashCommand`, `TaskSlashCommand`, `IssueSlashCommand` вЂ” moved from `InfiniContext.Cli/SlashCommands/` to `InfiniContext.Core/SlashCommands/`.

**Stays in `InfiniContext.Cli/`:**
- `ConsoleSlashCommandRenderer : ISlashCommandRenderer` вЂ” Spectre-backed implementation.
- `MultiSessionHost` вЂ” minus the command classes it used to own; still implements `IReplActions`.

**New in `InfiniContext.Gui/`:**
- `GuiSlashCommandRenderer : ISlashCommandRenderer` вЂ” appends to a new `CommandOutputLog` ObservableCollection on `MainWindowViewModel`; surfaces via a flyout / popup panel that opens when a command runs and stays visible until the operator dismisses or until the next command opens.
- `GuiReplActions : IReplActions` вЂ” `RequestExit` в†’ close `MainWindow`, `RequestResume`/`RequestSwitch`/`RequestFocus` в†’ existing VM commands.
- `MainWindowViewModel.HandleSlashCommandAsync(text)` вЂ” called from `SendToSessionAsync` when `text.StartsWith("/")`.
- `Views/CommandOutputPanel.axaml` вЂ” the rendering surface for command output.

## The enforcement: "impossible to register in one but not the other"

The single registration site is `SlashCommandRegistryFactory.Build(SlashCommandContext ctx)` in Core:

```csharp
public static class SlashCommandRegistryFactory
{
    public static SlashCommandRegistry Build()
    {
        return new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(),
            new NewSessionCommand(),
            new ListSessionsCommand(),
            new FocusCommand(),
            new BroadcastCommand(),
            new ResumeCommand(),
            new ApprovalCommand(),
            new SettingsCommand(),
            new StateCommand(),
            new CostCommand(),
            new QuitCommand(),
            new FlushSlashCommand(),
            new TaskSlashCommand(),
            new IssueSlashCommand(),
        });
    }
}
```

Both `InfiniContext.Cli` and `InfiniContext.Gui` call `SlashCommandRegistryFactory.Build()` exactly once at host startup. Neither project has access to a constructor that bypasses the factory:

- `SlashCommandRegistry`'s public constructor is preserved (it's the right shape for tests), but production code paths in both UIs use only `Factory.Build()`.
- A new build-time guard: a unit test in `InfiniContext.Tests/SlashCommands/RegistrySnapshotTests.cs` asserts the registry returned by `Factory.Build()` lists a known fixed set of command names. Adding a class without adding it to the factory fails the test; adding to the factory without writing the class fails compile. The snapshot is the single point that changes per command addition вЂ” Cli + Gui automatically inherit.

This is the load-bearing piece of the "impossible to register in one but not the other" guarantee:
1. Commands live in **Core**, so both projects compile against the same set of classes.
2. The registry is constructed by a **single factory in Core**, so both projects see the same set instances.
3. The **snapshot test** prevents quiet additions/removals from going unnoticed.

There is no public `Register(ISlashCommand)` extension surface; neither Cli nor Gui can mutate the registry. The factory is the only registration site, and it lives in Core.

## CLI в†” GUI wiring delta

**CLI (`MultiSessionHost.cs`):**
- Replace its direct `new SlashCommandRegistry(new ISlashCommand[]{...})` with `SlashCommandRegistryFactory.Build()`.
- Pass a fresh `ConsoleSlashCommandRenderer` into `SlashCommandContext` instead of `Write`/`Console`.
- No other change.

**GUI (`MainWindowViewModel.SendToSessionAsync`):**
```csharp
public async Task SendToSessionAsync(string text)
{
    if (text.StartsWith('/'))
    {
        var handled = await TryHandleSlashCommandAsync(text);
        if (handled) return;
        // Unknown slash command: show error in command-output panel.
    }
    // Existing send path
    await target.SendOrQueueAsync(text);
}

private async Task<bool> TryHandleSlashCommandAsync(string text)
{
    var (name, argLine) = SplitNameAndArgs(text);
    var cmd = _registry.TryResolve(name);
    if (cmd is null)
    {
        _renderer.WriteError($"Unknown command: {name}. Try /help.");
        return true; // Still handled вЂ” we showed an error rather than sending to claude.
    }
    var ctx = BuildContext();
    await cmd.ExecuteAsync(ctx, argLine, CancellationToken.None);
    return true;
}
```

The `BuildContext()` shape mirrors `MultiSessionHost`'s вЂ” same focused-session lookup, same approval-mode delegate wiring, same task-handler injection.

## Renderer adaptation details

CLI commands today call `_console.Write(new Table()...)` or `ctx.Write(string)` directly. Each commit-touchpoint in a command class becomes one or more renderer method calls. The mapping is mechanical:

| Today (CLI-coupled) | After (`ISlashCommandRenderer`) |
|---|---|
| `ctx.Write("text\n")` | `ctx.Renderer.WriteLine("text")` |
| `ctx.Console.Write(new Table().AddColumn(...))` | `ctx.Renderer.WriteTable(headers, rows)` |
| `AnsiConsole.MarkupLine("[red]X[/]")` | `ctx.Renderer.WriteError("X")` |
| `AnsiConsole.Prompt(new SelectionPrompt<...>())` | `await ctx.Renderer.PromptForChoiceAsync(label, options, ct)` |

`WriteTable` takes `IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows`. CLI renders a `Spectre.Console.Table`; GUI renders an Avalonia `Grid` inside the command-output panel.

## Testing strategy

- **Existing CLI command-execution tests** (`tests/InfiniContext.Tests/Cli/SlashCommands/*Tests.cs`) keep their shape; they're rewritten to construct a `FakeSlashCommandRenderer` (a `StringBuilder`-backed `ISlashCommandRenderer`) and assert against its captured output. Most tests already do this with `Action<string> Write` вЂ” the renaming is mechanical.
- **New `RegistrySnapshotTests`:** assert `SlashCommandRegistryFactory.Build().Commands.Select(c => c.Name).Order()` matches a hardcoded list. Updating the list IS the design intent вЂ” that's how you opt-in a new command.
- **New GUI handler test:** `MainWindowViewModelTests.SendToSessionAsync_slash_prefix_dispatches_to_registry` вЂ” fake registry + assert the right command was invoked with the right arg-line.

## Migration plan (slice order)

1. **Slice A вЂ” promote the framework.** Move `ISlashCommand`/`SlashCommandRegistry`/`SlashCommandContext`/`IReplActions` to Core. Make them public. Update Cli to consume from Core. No behavioural change. Build + tests green.
2. **Slice B вЂ” introduce `ISlashCommandRenderer`.** Add the interface in Core; add `ConsoleSlashCommandRenderer` (Spectre impl) in Cli; rewrite `SlashCommandContext` to expose `Renderer` instead of `Write`/`Console`; mechanically port each command's print-call to renderer methods. Existing CLI tests rewrite to `FakeSlashCommandRenderer`. Build + tests green.
3. **Slice C вЂ” promote the commands.** Move command CLASSES to `Core.SlashCommands`. Update namespaces; no logic change.
4. **Slice D вЂ” add the factory + snapshot test.** `SlashCommandRegistryFactory.Build()` in Core; Cli switches to it. `RegistrySnapshotTests` lists all commands. Both UIs now have one and only one registration site.
5. **Slice E вЂ” wire GUI dispatch.** `GuiSlashCommandRenderer`, `GuiReplActions`, `CommandOutputPanel.axaml`, the `text.StartsWith('/')` branch in `SendToSessionAsync`, the new VM test. End-to-end: type `/help` in GUI в†’ see command help in popup; type `/focus <prefix>` в†’ focus rotates; type `/task review 0012` в†’ invokes `TaskSlashCommand` against the focused session.

Slices AвЂ“D are each ~1-2h. Slice E is the biggest (~3-4h) because it introduces a new GUI surface (`CommandOutputPanel`) and the GUI renderer's Spectre-tag handling.

## What this does NOT do

- **No new commands.** Strictly a refactor + GUI wiring of the existing set. New commands are out of scope for this design; they're trivial once the framework is shared.
- **No keystroke-level command palette.** Just the `/`-prefix dispatch from the existing input box. A Command-Palette UX (Ctrl+P style fuzzy picker) is a follow-up.
- **No Spectreв†’Avalonia pixel parity.** The GUI renders command output its own way (Avalonia-native styling); the spec is "all the information from the CLI is visible to the GUI operator", not "identical visuals".

## Risk notes

- `IAnsiConsole`-flavoured commands (`/settings`, `/state`, `/list`) lean on Spectre's `Markup` syntax. The GUI renderer needs a markup parser (or accepts that markup tags leak through visibly). Recommend a small interpreter that strips/ignores unknown tags so the GUI output is at worst plain-text-with-bracket-garbage rather than crashing.
- `TaskSlashCommand`'s spawn path goes through `TryEnterTask` + `RequestResume` to resume into the new task session. Currently the GUI has no "resume" concept (it just opens a new row). `GuiReplActions.RequestResume` will translate to "spawn a row pointing at this task and focus it", which is the GUI's analogue.
- `MultiSessionHost`'s `RequestExit` ends the REPL; the GUI's would close the window. Operators may expect `/quit` to close JUST the focused session, not the whole window. Resolve in slice E вЂ” likely add an explicit `/closesession` and keep `/quit` as window-close.

## Open questions (defer to implementation)

- Should the GUI's `CommandOutputPanel` be a transient flyout (auto-dismiss on next input) or a docked side panel (operator dismisses manually)? Operator preference; default to flyout.
- Does `/broadcast` make sense in the GUI? CLI's prompt-all-sessions is naturally text-based; in the GUI the operator could just type and hit Send-to-all instead. Keep the command for parity; let operators use either.
