using TrainControl.Common.Communication;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Organizes the window commands and their input state.</summary>
public sealed partial class MainWindowViewModel
{
    private string _manualTarget = "6";
    private string _manualAction = "Switch normal";

    public string ManualCommandResult { get; private set; } = "No manual command sent.";

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
        object payload = ManualAction switch
        {
            "Switch normal" => new { blockId = ManualTarget, switchPosition = SwitchPosition.Normal },
            "Switch reverse" => new { blockId = ManualTarget, switchPosition = SwitchPosition.Reverse },
            "Signal red" => new { blockId = ManualTarget, signal = SignalState.Red },
            "Crossing closed" => new { blockId = ManualTarget, crossing = CrossingState.Closed },
            "Crossing open" => new { blockId = ManualTarget, crossing = CrossingState.Open },
            _ => throw new ArgumentException("Select a manual command.")
        };
        var message = MessageSerializer.CreateEnvelope("ApplyManual", payload);

        await SendAsync(message);
    }

}
