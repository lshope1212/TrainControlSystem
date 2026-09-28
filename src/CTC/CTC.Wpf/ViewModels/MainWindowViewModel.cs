using CTC.Core.Interfaces;
using CTC.Core.Services;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Minimal view model demonstrating that the WPF project can reach CTC.Core.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;

    public MainWindowViewModel()
        : this(new CTCService())
    {
    }

    public MainWindowViewModel(ICTCService ctc)
    {
        _ctc = ctc;
    }

    public string Title => "CTC Office";

    public string Status => "Architecture skeleton — no dispatching implemented yet.";

    public int TrainCount => _ctc.SystemState.Trains.Count;

    public string Mode => _ctc.DispatcherState.IsAutomaticMode ? "Automatic" : "Manual";
}
