using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Tasks;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// Top-level node in the left-column tree. Wraps either a real <see cref="TaskRecord"/> or the
/// synthetic "(unattached)" bucket for sessions without an <see cref="Core.Sessions.SessionContext.ActiveTaskId"/>.
/// Holds an observable collection of child <see cref="SessionRowViewModel"/>s and re-aggregates
/// its status glyph whenever children change.
/// </summary>
public sealed partial class TaskNodeViewModel : ObservableObject
{
    /// <summary>Null for the (unattached) synthetic node.</summary>
    public int? TaskId { get; }

    public TaskRecord? Record { get; }
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _statusGlyph = ViewModels.StatusGlyph.Empty;

    public string Title => Record?.Title ?? "(unattached)";
    public string HeaderId => TaskId is { } id ? $"{id:D4}" : "тАФ";
    public string HomeCwd => Record?.HomeCwd ?? "";
    public bool IsSynthetic => Record is null;

    public TaskNodeViewModel(TaskRecord? record)
    {
        Record = record;
        TaskId = record?.Id;
        Sessions.CollectionChanged += OnSessionsChanged;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Re-aggregate glyph + subscribe to new children's StatusGlyph changes.
        if (e.NewItems is not null)
        {
            foreach (SessionRowViewModel s in e.NewItems)
                s.PropertyChanged += OnSessionPropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (SessionRowViewModel s in e.OldItems)
                s.PropertyChanged -= OnSessionPropertyChanged;
        }
        RefreshGlyph();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionRowViewModel.StatusGlyph)) RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        StatusGlyph = ViewModels.StatusGlyph.Aggregate(Sessions.Select(s => s.StatusGlyph));
    }
}
