using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.TestUI.Wpf.ViewModels;
/// <summary>Stores editable simulated measurements until the operator sends them through the module interface.</summary>
public sealed class FeedbackRow(string id) : ViewModelBase
{
    public string BlockId { get; } = id;

    private OccupancyState _occupancy = OccupancyState.Clear;
    private bool _faulted;
    private bool? _reserved = false;

    public OccupancyState Occupancy
    {
        get => _occupancy;
        set => SetProperty(ref _occupancy, value);
    }

    public bool IsFaulted
    {
        get => _faulted;
        set => SetProperty(ref _faulted, value);
    }

    public bool? IsReserved
    {
        get => _reserved;
        set => SetProperty(ref _reserved, value);
    }

    public SignalState Signal { get; set; } = SignalState.Green;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Normal;

    public CrossingState Crossing { get; set; } = CrossingState.Closed;
}
