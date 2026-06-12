using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using InfiniContext.Gui.HeadlessTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace InfiniContext.Gui.HeadlessTests;

/// <summary>Assembly-level headless bootstrap (design.md section 5.2). One app builder per test
/// assembly; default PerTest session isolation (fresh Application + Dispatcher per test).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            // UseHeadlessDrawing=true (default): no Skia pixels, fast. Flip to false only if a
            // future test needs CaptureRenderedFrame() visual baselines.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Test stand-in for the production <c>App</c>. Loads the REAL application style list
/// (Styles/AppStyles.axaml, factored out of App.axaml for exactly this purpose) so headless
/// tests exercise the production theme and control templates, but never runs the production
/// composition root (SessionHost process spawn, ~/.infinicontext writes).</summary>
public sealed class TestApp : Application
{
    public override void Initialize() =>
        Styles.Add(new StyleInclude(new Uri("avares://InfiniContext.Gui/"))
        {
            Source = new Uri("avares://InfiniContext.Gui/Styles/AppStyles.axaml"),
        });
}
