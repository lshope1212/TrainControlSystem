using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the Track Controller -> CTC block status input by sending a real
/// <see cref="BlockStatusMessage"/> to the running CTC. The block ID is deliberately not
/// validated here, so CTC's own handling of unknown blocks can be exercised.
/// </summary>
public class BlockStatusInputViewModel : CtcInputViewModelBase
{
    private string _blockId = string.Empty;
    private OccupancyState _selectedOccupancy = OccupancyState.Unknown;
    private SignalState _selectedSignal = SignalState.Unknown;
    private SwitchPosition _selectedSwitch = SwitchPosition.Unknown;
    private CrossingState _selectedCrossing = CrossingState.Unknown;

    public BlockStatusInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        SendCommand = new AsyncRelayCommand(_ => SendAsync());
    }

    // User-entered block
    public string BlockId
    {
        get => _blockId;
        set => SetProperty(ref _blockId, value);
    }

    // Available enum values for the ComboBoxes
    public OccupancyState[] OccupancyOptions { get; } = Enum.GetValues<OccupancyState>();

    public SignalState[] SignalOptions { get; } = Enum.GetValues<SignalState>();

    public SwitchPosition[] SwitchOptions { get; } = Enum.GetValues<SwitchPosition>();

    public CrossingState[] CrossingOptions { get; } = Enum.GetValues<CrossingState>();

    // Selected values from the ComboBoxes
    public OccupancyState SelectedOccupancy
    {
        get => _selectedOccupancy;
        set => SetProperty(ref _selectedOccupancy, value);
    }

    public SignalState SelectedSignal
    {
        get => _selectedSignal;
        set => SetProperty(ref _selectedSignal, value);
    }

    public SwitchPosition SelectedSwitch
    {
        get => _selectedSwitch;
        set => SetProperty(ref _selectedSwitch, value);
    }

    public CrossingState SelectedCrossing
    {
        get => _selectedCrossing;
        set => SetProperty(ref _selectedCrossing, value);
    }

    public ICommand SendCommand { get; }

    private Task SendAsync()
    {
        // The SAME contract object the real Track Controller will eventually send to CTC.
        var message = new BlockStatusMessage
        {
            BlockId = BlockId.Trim(),
            Occupancy = SelectedOccupancy,
            Signal = SelectedSignal,
            Switch = SelectedSwitch,
            Crossing = SelectedCrossing,
        };

        return SendToCtcAsync(message, $" for Block {message.BlockId}");
    }
}
