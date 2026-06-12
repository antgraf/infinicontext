using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using InfiniContext.Gui.Views;
using InfiniContext.Tests.Gui;

namespace InfiniContext.Gui.HeadlessTests;

/// <summary>Task 0020 Phase 1 proof-of-concept: the real <see cref="MainWindow"/> (production
/// XAML + production theme via TestApp) realized on the headless platform, driven through
/// simulated keyboard input. The seed list in design.md section 5.5 grows from here.</summary>
public class MainWindowPocTests
{
    [AvaloniaFact]
    public void MainWindow_realizes_under_production_styles_with_send_and_prompt_gated()
    {
        var vm = TestRig.CreateMainVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<TextBox>("PromptBox");
        prompt.Should().NotBeNull();
        prompt!.GetVisualChildren().Should().NotBeEmpty(
            "the Fluent template from Styles/AppStyles.axaml should have been applied - an empty " +
            "visual subtree means the production theme did not load under the headless TestApp");
        prompt.IsEnabled.Should().BeFalse("no focused session -> the prompt box's IsEnabled binding gates it off");

        var send = window.FindControl<Button>("SendButton");
        send.Should().NotBeNull();
        send!.IsEffectivelyEnabled.Should().BeFalse("no focused session -> CanSendPrompt is false");
    }

    [AvaloniaFact]
    public void Typed_prompt_enables_send_and_ctrl_enter_delivers_it_to_the_session()
    {
        var vm = TestRig.CreateMainVm();
        var factory = new SessionContextFactory(activeTaskId: null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.FocusedSession = TestRig.CreateRow(factory);
        Dispatcher.UIThread.RunJobs();

        var prompt = window.FindControl<TextBox>("PromptBox")!;
        prompt.Focus().Should().BeTrue("a focused session enables the prompt box, making it focusable");
        window.KeyTextInput("hello");
        Dispatcher.UIThread.RunJobs();

        vm.PromptText.Should().Be("hello",
            "keystrokes must flow into the VM as they are typed - the task 0017 focus-contract regression surface");
        var send = window.FindControl<Button>("SendButton")!;
        send.IsEffectivelyEnabled.Should().BeTrue("free session is ready at ctor and the prompt is non-empty");

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        factory.Session.SentMessages.Should().ContainSingle().Which.Should().Be("hello");
        vm.PromptText.Should().BeEmpty("SendPromptAsync clears the box for the next message");
    }
}
