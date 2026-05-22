using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Sessions;
using InfiniContext.Gui.Services;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// View over one session's <see cref="TranscriptModel"/>. Subscribes to
/// <see cref="TranscriptModel.FragmentAppended"/> on construction and marshals each appended
/// fragment onto the UI thread via the injected <see cref="IUiDispatcher"/>. <see cref="Snapshot"/>
/// pre-loads any fragments the model already held (catches us up after a focus rotation).
/// </summary>
public sealed partial class TranscriptViewModel : ObservableObject, IDisposable
{
    private readonly TranscriptModel _model;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    public ObservableCollection<string> Fragments { get; } = new();

    public TranscriptViewModel(TranscriptModel model, IUiDispatcher dispatcher)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        foreach (var f in _model.Snapshot()) Fragments.Add(f);
        _model.FragmentAppended += OnFragmentAppended;
    }

    private void OnFragmentAppended(string fragment)
    {
        // Hot path - FragmentAppended fires on the SessionTranscriptReader's loop thread. Marshal
        // back to UI; ignore handler exceptions so a bad subscriber doesn't take down the reader.
        if (_dispatcher.CheckAccess())
        {
            Fragments.Add(fragment);
        }
        else
        {
            _dispatcher.Post(() => Fragments.Add(fragment));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.FragmentAppended -= OnFragmentAppended;
    }
}
