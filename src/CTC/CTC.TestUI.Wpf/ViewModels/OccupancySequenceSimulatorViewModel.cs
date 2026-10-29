using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// TEST-ONLY convenience for demonstrating CTC's sequential-occupancy train tracking. Imitates
/// track circuits as something moves along a typed list of blocks, one step per click, sending
/// only ordinary anonymous <see cref="BlockStatusMessage"/>s, exactly like the Track Controller.
/// </summary>
/// <remarks>
/// It deliberately knows nothing about trains: no train ID is entered or sent, and it never asks
/// CTC which train it will associate with the occupancy. Each step reports the next block
/// Occupied BEFORE the previous block Clear, so CTC briefly sees both occupied, as when a train
/// spans a block boundary. Signal, switch and crossing are reported Unknown and maintenance Open,
/// because a BlockStatusMessage always carries the block's whole status.
/// </remarks>
public class OccupancySequenceSimulatorViewModel : CtcInputViewModelBase
{
    private readonly List<string> _route = new List<string>();

    // Blocks this simulator has reported Occupied and not yet Clear.
    private readonly List<string> _occupiedBlocks = new List<string>();

    private string _routeText = "A1,A2,A3,A4,A5";
    private int _frontIndex = -1;

    public OccupancySequenceSimulatorViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        StartCommand = new AsyncRelayCommand(_ => StartAsync());
        NextStepCommand = new AsyncRelayCommand(_ => NextStepAsync(), _ => IsStarted && _frontIndex + 1 < _route.Count);
        ResetCommand = new AsyncRelayCommand(_ => ResetAsync(), _ => _occupiedBlocks.Count > 0 || IsStarted);
    }

    /// <summary>Comma-separated block IDs in travel order, e.g. "A1,A2,A3".</summary>
    public string RouteText
    {
        get => _routeText;
        set => SetProperty(ref _routeText, value);
    }

    /// <summary>Where the simulated occupancy has reached.</summary>
    public string Progress => IsStarted
        ? $"Front of occupancy: {_route[_frontIndex]} (step {_frontIndex + 1} of {_route.Count}). Occupied: {string.Join(", ", _occupiedBlocks)}."
        : "Not started.";

    /// <summary>Clears anything this simulator left occupied, then reports the first block Occupied.</summary>
    public ICommand StartCommand { get; }

    /// <summary>Reports the next block Occupied, then the previous block Clear.</summary>
    public ICommand NextStepCommand { get; }

    /// <summary>Reports every block this simulator left occupied Clear and stops the sequence.</summary>
    public ICommand ResetCommand { get; }

    private bool IsStarted => _frontIndex >= 0;

    private async Task StartAsync()
    {
        var route = RouteText
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (route.Count == 0)
        {
            Result = "Enter at least one block ID, e.g. A1,A2,A3.";
            return;
        }

        if (!await ClearOccupiedBlocksAsync())
        {
            return;
        }

        _route.Clear();
        _route.AddRange(route);
        _frontIndex = -1;

        if (await SendOccupancyAsync(_route[0], OccupancyState.Occupied))
        {
            _frontIndex = 0;
        }

        OnPropertyChanged(nameof(Progress));
    }

    private async Task NextStepAsync()
    {
        string previous = _route[_frontIndex];
        string next = _route[_frontIndex + 1];

        // Next block first: the train enters it before its rear leaves the previous one.
        if (!await SendOccupancyAsync(next, OccupancyState.Occupied))
        {
            return;
        }

        _frontIndex++;
        OnPropertyChanged(nameof(Progress));

        await SendOccupancyAsync(previous, OccupancyState.Clear);
        OnPropertyChanged(nameof(Progress));
    }

    private async Task ResetAsync()
    {
        if (await ClearOccupiedBlocksAsync())
        {
            _frontIndex = -1;
            Result = "Occupancy sequence reset.";
        }

        OnPropertyChanged(nameof(Progress));
    }

    /// <returns>True if every block this simulator occupied was reported Clear.</returns>
    private async Task<bool> ClearOccupiedBlocksAsync()
    {
        foreach (var blockId in _occupiedBlocks.ToList())
        {
            if (!await SendOccupancyAsync(blockId, OccupancyState.Clear))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SendOccupancyAsync(string blockId, OccupancyState occupancy)
    {
        // The SAME anonymous contract object the real Track Controller sends: no train ID.
        var message = new BlockStatusMessage
        {
            BlockId = blockId,
            Occupancy = occupancy,
            Maintenance = MaintenanceState.Open,
        };

        if (!await SendToCtcAsync(message, $" for Block {blockId} ({occupancy})"))
        {
            return false;
        }

        _occupiedBlocks.Remove(blockId);
        if (occupancy == OccupancyState.Occupied)
        {
            _occupiedBlocks.Add(blockId);
        }

        return true;
    }
}
