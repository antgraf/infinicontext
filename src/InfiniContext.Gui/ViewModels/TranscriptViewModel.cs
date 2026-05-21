using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using InfiniContext.Core.Sessions;

namespace InfiniContext.Gui.ViewModels;

/// <summary>
/// View over one session's <see cref="TranscriptModel"/>. Subscribes to
/// <see cref="TranscriptModel.FragmentAppended"/> on construction and marshals each appended
/// fragment onto the UI thread via <see cref="Dispatcher.UIThread.Post"/>. <see cref="Snapshot"/>
/// pre-loads any fragments the model already held (catches us up after a focus rotation).
/// </summary>
public sealed partial class TranscriptViewModel : ObservableObject, IDisposable
{
    private readonly TranscriptModel _model;
    private bool _disposed;

    public ObservableCollection<string> Fragments { get; } = new();

    public TranscriptViewModel(TranscriptModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        foreach (var f in _model.Snapshot()) Fragments.Add(f);
        _model.FragmentAppended += OnFragmentAppended;
    }

    private void OnFragmentAppended(string fragment)
    {
        // Hot path - FragmentAppended fires on the SessionTranscriptReader's loop thread. Marshal
        // back to UI; ignore handler exceptions so a bad subscriber doesn't take down the reader.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Fragments.Add(fragment);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Fragments.Add(fragment));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _model.FragmentAppended -= OnFragmentAppended;
    }
}
