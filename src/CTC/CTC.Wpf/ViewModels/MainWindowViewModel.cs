using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Wpf.Commands;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// View model for the CTC office window. Translates user intent into CTC service calls;
/// it never builds outgoing contract messages itself.
/// </summary>
/// <remarks>
/// CTC state can change without any user action (messages arriving from other modules),
/// so the view model refreshes from <see cref="ICTCService.StateChanged"/>, which the
/// application always raises on the UI thread.
/// </remarks>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private BlockViewModel? _selectedBlock;
    private CtcLineState? _selectedLine;
    private string _communicationStatus = "No requests sent.";
    private string _inboundStatus = "No messages received.";
    private string _lastTerritoryUpdate = "--";
    private bool _isMaintenanceMode;

    public MainWindowViewModel(ICTCService ctc)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

        ToggleMaintenanceModeCommand = new RelayCommand(_ => IsMaintenanceMode = !IsMaintenanceMode);
        CloseSelectedBlockCommand = new AsyncRelayCommand(_ => CloseSelectedBlockAsync(), _ => CanCloseSelectedBlock());
        ToggleSelectedBlockSwitchCommand = new AsyncRelayCommand(_ => ToggleSelectedBlockSwitchAsync(), _ => CanToggleSelectedBlockSwitch());

        //TODO other command implementations

        ScheduleBuilder = new ScheduleBuilderViewModel(_ctc);

        RebuildLines();
        RebuildBlocks();
        RebuildDispatchQueue();
        RebuildDispatchedTrains();
        _ctc.StateChanged += OnCtcStateChanged;
    }

    /// <summary>Schedule Builder tab, scoped to <see cref="SelectedLine"/>.</summary>
    public ScheduleBuilderViewModel ScheduleBuilder { get; }

    /// <summary>Queued trains on the selected line, ordered by departure time.</summary>
    public ObservableCollection<DispatchQueueEntryViewModel> DispatchQueue { get; } = new ObservableCollection<DispatchQueueEntryViewModel>();

    public int QueuedTrainCount => DispatchQueue.Count;

    /// <summary>
    /// Trains CTC has released on the selected line, with the speed/authority CTC suggested.
    /// Backed by CTC's DispatchedTrains state.
    /// </summary>
    public ObservableCollection<DispatchedTrainViewModel> DispatchedTrains { get; } = new ObservableCollection<DispatchedTrainViewModel>();

    public int DispatchedTrainCount => DispatchedTrains.Count;

    public string Title => "CTC Office";

    public string SystemTimeDisplay => _ctc.State.SystemTime.ToString(@"hh\:mm\:ss");

    /// <summary>Every block in the current layout, across all lines.</summary>
    public ObservableCollection<BlockViewModel> Blocks { get; } = new ObservableCollection<BlockViewModel>();

    public ObservableCollection<CtcLineState> AvailableLines { get; } = new ObservableCollection<CtcLineState>();

    public int OccupiedBlockCount => Blocks.Count(block => block.IsOccupied);

    /// <summary>Wall-clock time CTC state last changed.</summary>
    public string LastTerritoryUpdate
    {
        get => _lastTerritoryUpdate;
        private set => SetProperty(ref _lastTerritoryUpdate, value);
    }

    /// <summary>
    /// Block chosen by the dispatcher. Set from the block selector until the territory
    /// display renders CTC blocks.
    /// </summary>
    public BlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (SetProperty(ref _selectedBlock, value))
            {
                // Block-control availability depends on the selected block.
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Result of the most recent request sent to another subsystem.</summary>
    public string CommunicationStatus
    {
        get => _communicationStatus;
        private set => SetProperty(ref _communicationStatus, value);
    }

    /// <summary>Outcome of the most recent message received from another subsystem.</summary>
    public string InboundStatus
    {
        get => _inboundStatus;
        set => SetProperty(ref _inboundStatus, value);
    }

    public CtcLineState? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (SetProperty(ref _selectedLine, value))
            {
                // Later this can rebuild/filter the territory display for only the selected line. TODO
                ScheduleBuilder.Line = value;
                RebuildDispatchQueue();
                RebuildDispatchedTrains();
            }
        }
    }

    /// <summary>
    /// Local operator mode that unlocks the Block Controls. Separate from a block's
    /// <see cref="MaintenanceState"/>: toggling it changes no CTC state and sends no messages.
    /// </summary>
    public bool IsMaintenanceMode
    {
        get => _isMaintenanceMode;
        private set
        {
            if (SetProperty(ref _isMaintenanceMode, value))
            {
                OnPropertyChanged(nameof(MaintenanceModeButtonText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string MaintenanceModeButtonText => IsMaintenanceMode ? "Maintenance Mode: ON" : "Maintenance Mode: OFF";

    public ICommand ToggleMaintenanceModeCommand { get; }

    public ICommand CloseSelectedBlockCommand { get; }

    private bool CanCloseSelectedBlock() =>
        IsMaintenanceMode && SelectedBlock is not null && SelectedBlock.RequestedMaintenanceState != MaintenanceState.Closed;

    private async Task CloseSelectedBlockAsync()
    {
        var block = SelectedBlock;
        if (block is null)
        {
            return;
        }

        CommunicationStatus = $"Sending maintenance request for Block {block.BlockId}...";

        try
        {
            await _ctc.CloseBlockAsync(block.BlockId);
            CommunicationStatus = $"Maintenance request sent for Block {block.BlockId}.";
        }
        catch (Exception ex)
        {
            // MessageSendException carries a dispatcher-readable reason (e.g. "Track
            // Controller is not connected."). Everything is caught because this runs
            // from an async void command and must never crash the UI.
            CommunicationStatus = $"Unable to send maintenance request: {ex.Message}";
        }
        finally
        {
            block.Refresh();
        }
    }

    public ICommand ToggleSelectedBlockSwitchCommand { get; }

    // Unknown means CTC has no reported position to toggle from, so nothing is requested.
    private bool CanToggleSelectedBlockSwitch() =>
        IsMaintenanceMode
        && SelectedBlock is not null
        && SelectedBlock.HasSwitch
        && SelectedBlock.SwitchPosition != SwitchPosition.Unknown;

    private async Task ToggleSelectedBlockSwitchAsync()
    {
        var block = SelectedBlock;
        if (block is null || !block.HasSwitch || block.SwitchPosition == SwitchPosition.Unknown)
        {
            return;
        }

        var requestedPosition = block.SwitchPosition == SwitchPosition.Normal ? SwitchPosition.Reverse : SwitchPosition.Normal;

        CommunicationStatus = $"Requesting Block {block.BlockId} switch {requestedPosition}...";

        try
        {
            // The displayed switch position is not changed here; it updates only when the
            // Track Controller reports the new position in a BlockStatusMessage.
            await _ctc.SetSwitchPositionAsync(block.BlockId, requestedPosition);
            CommunicationStatus = $"Switch position request sent for Block {block.BlockId}: {requestedPosition}.";
        }
        catch (Exception ex)
        {
            // Everything is caught because this runs from an async void command and must
            // never crash the UI.
            CommunicationStatus = $"Unable to send switch request: {ex.Message}";
        }
    }

    private void OnCtcStateChanged(object? sender, CtcStateChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case CtcStateChangeKind.TrackLayout:
                // The old CtcBlockState objects were discarded, so the adapters must be rebuilt.
                RebuildLines();
                RebuildBlocks();
                MarkTerritoryUpdated();
                break;

            case CtcStateChangeKind.BlockStatus:
            case CtcStateChangeKind.MaintenanceRequest:
                foreach (var block in Blocks)
                {
                    block.Refresh();
                }

                OnPropertyChanged(nameof(OccupiedBlockCount));
                MarkTerritoryUpdated();
                break;

            case CtcStateChangeKind.SystemTime:
                // Shows the time CTC actually received; CTC has no display timer of its own.
                OnPropertyChanged(nameof(SystemTimeDisplay));
                break;

            case CtcStateChangeKind.Schedule:
            case CtcStateChangeKind.DispatchQueue:
                RebuildDispatchQueue();
                break;

            case CtcStateChangeKind.TrainDispatched:
                RebuildDispatchQueue();
                RebuildDispatchedTrains();
                CommunicationStatus = $"{TrainIds.DisplayName(e.TrainId ?? string.Empty)} dispatched at {SystemTimeDisplay} (movement suggestion and request sent)."
                    + (e.Message is null ? string.Empty : $" Warning: {e.Message}");
                break;

            case CtcStateChangeKind.DispatchFailed:
                // The train is still queued; CTC retries on the next system time update.
                CommunicationStatus = $"Unable to dispatch {TrainIds.DisplayName(e.TrainId ?? string.Empty)}: {e.Message} Will retry on the next time update.";
                break;
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void MarkTerritoryUpdated() => LastTerritoryUpdate = DateTime.Now.ToString("HH:mm:ss");

    private void RebuildBlocks()
    {
        var selectedBlockId = SelectedBlock?.BlockId;

        Blocks.Clear();
        foreach (var block in _ctc.State.Lines.SelectMany(line => line.Blocks))
        {
            Blocks.Add(new BlockViewModel(block));
        }

        // Keep the dispatcher's selection when the new layout still has that block.
        SelectedBlock = Blocks.FirstOrDefault(block => block.BlockId == selectedBlockId);
    }

    private void RebuildDispatchQueue()
    {
        DispatchQueue.Clear();

        if (SelectedLine is not null)
        {
            // CTC keeps the queue ordered by departure time.
            foreach (var entry in _ctc.State.DispatchQueue.Where(entry => entry.LineId == SelectedLine.LineId))
            {
                DispatchQueue.Add(new DispatchQueueEntryViewModel(entry));
            }
        }

        OnPropertyChanged(nameof(QueuedTrainCount));
    }

    private void RebuildDispatchedTrains()
    {
        DispatchedTrains.Clear();

        var lineId = SelectedLine?.LineId;
        foreach (var train in _ctc.State.DispatchedTrains.Where(train => train.LineId == lineId))
        {
            DispatchedTrains.Add(new DispatchedTrainViewModel(train));
        }

        OnPropertyChanged(nameof(DispatchedTrainCount));
    }

    private void RebuildLines()
    {
        string? selectedLineId = SelectedLine?.LineId;

        AvailableLines.Clear();

        foreach (var line in _ctc.State.Lines)
        {
            AvailableLines.Add(line);
        }

        // Try to preserve the currently selected line.
        SelectedLine = AvailableLines.FirstOrDefault(line => line.LineId == selectedLineId);

        // If there was no previous selection, automatically select the first imported line.
        SelectedLine ??= AvailableLines.FirstOrDefault();
    }
}
