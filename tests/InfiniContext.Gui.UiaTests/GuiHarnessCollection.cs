using InfiniContext.Gui.UiaDriver;

namespace InfiniContext.Gui.UiaTests;

/// <summary>One launched GUI shared by every test in the collection (design.md section 6.2);
/// xunit only constructs it when a non-skipped test actually runs. Tests that mutate global
/// GUI state must restore it (or take a private harness - the slow path, used sparingly).</summary>
public sealed class GuiHarnessFixture : IDisposable
{
    public GuiAppHarness Harness { get; } = new();

    public void Dispose() => Harness.Dispose();
}

[CollectionDefinition(Name)]
public sealed class GuiHarnessCollection : ICollectionFixture<GuiHarnessFixture>
{
    public const string Name = "gui-uia";
}
