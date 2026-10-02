using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.SampleData;
using CTC.TestUI.Wpf.Services;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the Track Model -> CTC "track layout" input by sending a real
/// TrackLayoutMessage to the running CTC.
/// </summary>
public class TrackLayoutInputViewModel : CtcInputViewModelBase
{
    public TrackLayoutInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        Result = "No layout sent.";
        SendSampleLayoutCommand = new AsyncRelayCommand(_ => SendSampleLayoutAsync());
    }

    public ICommand SendSampleLayoutCommand { get; }

    private Task SendSampleLayoutAsync()
    {
        var message = SampleTrackLayout.Create();

        return SendToCtcAsync(message, $" ({message.Lines.Count} line(s), {message.Lines.Sum(l => l.Blocks.Count)} block(s))");
    }
}
