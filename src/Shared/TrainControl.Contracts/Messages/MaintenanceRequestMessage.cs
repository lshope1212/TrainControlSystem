using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to open or close a block for maintenance.
/// Direction: CTC -> Track Controller.
/// </summary>
public class MaintenanceRequestMessage
{
    public string BlockId { get; set; } = string.Empty;

    public MaintenanceState RequestedState { get; set; } = MaintenanceState.Open;
}
