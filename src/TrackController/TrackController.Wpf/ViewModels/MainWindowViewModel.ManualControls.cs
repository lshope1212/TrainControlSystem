using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Organizes the window commands and their input state.</summary>
public sealed partial class MainWindowViewModel
{
    private string _manualTarget = "6";
    private string _manualAction = "Switch normal";

    public string ManualTarget
    {
        get => _manualTarget;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _manualTarget, value);
            }
        }
    }

    public string ManualAction
    {
        get => _manualAction;
        set => SetProperty(ref _manualAction, value);
    }

    public string[] ManualActions { get; } = ["Switch normal", "Switch reverse", "Signal red", "Crossing closed", "Crossing open"];

    private async Task SendManualAsync()
    {
        var message = new WaysideManualCommandMessage
        {
            BlockId = ManualTarget
        };
        switch (ManualAction)
        {
            case "Switch normal":
                message.Switch = SwitchPosition.Normal;
                break;
            case "Switch reverse":
                message.Switch = SwitchPosition.Reverse;
                break;
            case "Signal red":
                message.Signal = SignalState.Red;
                break;
            case "Crossing closed":
                message.Crossing = CrossingState.Closed;
                break;
            case "Crossing open":
                message.Crossing = CrossingState.Open;
                break;
            default:
                throw new ArgumentException("Select a manual command.");
        }

        await SendAsync(message);
    }

}
