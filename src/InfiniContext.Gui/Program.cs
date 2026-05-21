using Avalonia;
using InfiniContext.Core.Configuration;
using InfiniContext.Core.Logging;
using InfiniContext.Core.Mcp;

namespace InfiniContext.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Sidecar mode: claude spawned us as the MCP approval server (ApprovalSidecarLauncher
        // re-uses Environment.ProcessPath, so when the GUI is the host claude spawns
        // InfiniContext.Gui.exe --mcp-approval-server <pipe>). MUST short-circuit before Avalonia
        // bootstraps, otherwise: (a) we'd pop a second MainWindow, and (b) the sidecar's stdout -
        // reserved for JSON-RPC - would race the Avalonia bring-up.
        if (args.Length >= 2 && args[0] == "--mcp-approval-server")
        {
            return RunSidecarAsync(args[1]).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> RunSidecarAsync(string pipeName)
    {
        InfiniContextSettings settings;
        try { settings = InfiniContextSettingsLoader.Load().Settings; }
        catch { settings = InfiniContextSettings.Defaults; }

        await using var logging = InfiniContextLogger.ConfigureFromHostStartup(settings.Logging, prefix: "sidecar");
        logging.Logger.Information("sidecar invoked (from gui) pipe={PipeName}", pipeName);
        return await ApprovalSidecarHost.RunAsync(
            pipeName,
            logging.Logger,
            connectTimeoutSeconds: settings.Mcp.SidecarConnectTimeoutSeconds);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
