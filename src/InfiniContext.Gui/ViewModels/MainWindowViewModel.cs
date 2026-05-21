using CommunityToolkit.Mvvm.ComponentModel;

namespace InfiniContext.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _greeting = "InfiniContext GUI тАФ Phase 3B scaffold. Sessions / Tasks / Activity views not wired yet.";
}
