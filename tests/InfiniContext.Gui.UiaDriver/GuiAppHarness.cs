using System.Diagnostics;
using FlaUI.UIA3;
using InfiniContext.Tests;

namespace InfiniContext.Gui.UiaDriver;

/// <summary>
/// Launches a hermetic <c>InfiniContext.Gui.exe</c> for UIA-level tests and the ad-hoc probe
/// (task 0020 design.md section 6.1). Hermetic = the same env contract the tmp/icx-smoke
/// scripts proved: <c>CLAUDE_EXE</c> points at FakeClaude, <c>FAKE_CLAUDE_SCRIPT</c> at a
/// per-sandbox action script, the tasks DB and gui-settings (window placement) are redirected
/// into a per-instance temp sandbox, and every inherited <c>INFINICONTEXT__*</c> variable is
/// stripped so a dev machine's overrides can't leak in (house test rule).
///
/// <para>Known gap (design.md prerequisite P3): the session index at
/// <c>~/.infinicontext/index.json</c> has no override yet, so tests that SPAWN sessions would
/// pollute the operator's real index. Until P3 lands, keep UIA tests to flows that don't spawn
/// sessions.</para>
/// </summary>
public sealed class GuiAppHarness : IDisposable
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Minimal session stand-in: FakeClaude idles on stdin so any accidentally spawned
    /// session stays alive without scripted turns. Tests that need conversation traffic pass
    /// their own script JSON to the ctor.</summary>
    private const string DefaultFakeClaudeScript =
        """{ "actions": [ { "type": "wait-stdin", "lines": 9999 } ] }""";

    /// <summary>Windowed at 60,60 sized 960x600 - fits the 1024x768 desktop of a GitHub-hosted
    /// windows-latest runner (design.md section 6.4: never depend on screen coordinates, but DO
    /// pin the window size so layout is deterministic).</summary>
    private const string DefaultGuiSettings =
        """{ "version": 1, "windowWidth": 960, "windowHeight": 600, "windowX": 60, "windowY": 60, "windowMaximized": false }""";

    private readonly FlaUI.Core.Application _app;
    private readonly UIA3Automation _automation;
    private bool _disposed;

    /// <summary>Per-instance temp dir holding the FakeClaude script, tasks DB, gui-settings and
    /// the GUI's <c>logs/</c> (the process cwd is the sandbox). Deleted on dispose.</summary>
    public string SandboxDir { get; }

    public MainWindowPage MainWindow { get; }

    public GuiAppHarness(string? fakeClaudeScriptJson = null)
    {
        var configuration = Environment.GetEnvironmentVariable("ICX_UIA_CONFIGURATION") ?? "Release";
        var root = TestPaths.RepoRoot;
        var guiExe = Path.Combine(root, "src", "InfiniContext.Gui", "bin", configuration, "net10.0", "InfiniContext.Gui.exe");
        var fakeClaudeExe = Path.Combine(root, "tests", "FakeClaude", "bin", configuration, "net10.0", "FakeClaude.exe");
        foreach (var exe in new[] { guiExe, fakeClaudeExe })
        {
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException(
                    $"'{exe}' not found. Build the solution in configuration '{configuration}' first " +
                    "(override the configuration via ICX_UIA_CONFIGURATION).");
            }
        }

        SandboxDir = Path.Combine(Path.GetTempPath(), "icx-uia", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(SandboxDir);
        var scriptPath = Path.Combine(SandboxDir, "fake-claude-script.json");
        File.WriteAllText(scriptPath, fakeClaudeScriptJson ?? DefaultFakeClaudeScript);
        var guiSettingsPath = Path.Combine(SandboxDir, "gui-settings.json");
        File.WriteAllText(guiSettingsPath, DefaultGuiSettings);

        var startInfo = new ProcessStartInfo
        {
            FileName = guiExe,
            // Settings auto-discovery happens in the working directory; an empty sandbox means
            // in-code defaults - never the repo's settings.json or a dev's settings.user.json.
            WorkingDirectory = SandboxDir,
            UseShellExecute = false,
        };
        foreach (var key in startInfo.Environment.Keys
                     .Where(k => k.StartsWith("INFINICONTEXT__", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            startInfo.Environment.Remove(key);
        }
        startInfo.Environment["CLAUDE_EXE"] = fakeClaudeExe;
        startInfo.Environment["FAKE_CLAUDE_SCRIPT"] = scriptPath;
        startInfo.Environment["INFINICONTEXT__TASKS__DBPATH"] = Path.Combine(SandboxDir, "smoke.db");
        startInfo.Environment["INFINICONTEXT__GUI__SETTINGSPATH"] = guiSettingsPath;

        _automation = new UIA3Automation();
        _app = FlaUI.Core.Application.Launch(startInfo);
        try
        {
            var window = _app.GetMainWindow(_automation, LaunchTimeout)
                ?? throw new InvalidOperationException(
                    $"InfiniContext.Gui main window did not appear within {LaunchTimeout.TotalSeconds:0}s.");
            MainWindow = new MainWindowPage(window, _automation);
        }
        catch
        {
            DisposeCore();
            throw;
        }
    }

    /// <summary>Screenshot the whole main window to <paramref name="path"/> (PNG). Best-effort:
    /// returns false instead of throwing so failure handlers can chain diagnostics.</summary>
    public bool TryScreenshot(string path)
    {
        try
        {
            using var image = FlaUI.Core.Capturing.Capture.Element(MainWindow.Raw);
            image.ToFile(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        try
        {
            // Graceful first: WM_CLOSE lets the host run its shutdown drain (Serilog flush,
            // session teardown). The Job Object supervisor guarantees child cleanup either way;
            // the hard kill below is the backstop for a hung shutdown.
            if (!_app.HasExited)
            {
                _app.Close();
                using var process = Process.GetProcessById(_app.ProcessId);
                if (!process.WaitForExit((int)GracefulCloseTimeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // Process already gone (or unobservable) - nothing left to clean up.
        }
        finally
        {
            _automation.Dispose();
            _app.Dispose();
            try { Directory.Delete(SandboxDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
