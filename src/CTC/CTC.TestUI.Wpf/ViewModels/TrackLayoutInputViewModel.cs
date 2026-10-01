using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.SampleData;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the Track Model -> CTC "track layout" input by feeding a real
/// TrackLayoutMessage into CTCService.ApplyTrackLayout.
/// </summary>
public class TrackLayoutInputViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private readonly Action _onCtcStateChanged;
    private string _result = "No layout applied.";

    public TrackLayoutInputViewModel(ICTCService ctc, Action onCtcStateChanged)
    {
        _ctc = ctc;
        _onCtcStateChanged = onCtcStateChanged;
        ApplySampleLayoutCommand = new RelayCommand(_ => ApplySampleLayout());
    }

    public ICommand ApplySampleLayoutCommand { get; }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    private void ApplySampleLayout()
    {
        var message = SampleTrackLayout.Create();

        try
        {
            _ctc.ApplyTrackLayout(message);
            Result = $"ApplyTrackLayout: {message.Lines.Count} line(s), {message.Lines.Sum(l => l.Blocks.Count)} block(s).";
        }
        catch (Exception ex)
        {
            Result = $"ApplyTrackLayout failed: {ex.GetType().Name}: {ex.Message}";
        }

        _onCtcStateChanged();
    }
}
