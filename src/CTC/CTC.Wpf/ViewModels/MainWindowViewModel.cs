using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Scheduling;
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
    private DispatchedTrainViewModel? _selectedDispatchedTrain;
    private string? _editedDestinationBlockId;
    private string _editedArrivalTime = string.Empty;
    private string _editedDwellTime = string.Empty;
    private MovementPlanStopViewModel? _selectedStop;
    private bool _isRebuildingDispatchedTrains;
    private bool _isLoadingEditedPlan;

    public MainWindowViewModel(ICTCService ctc)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

        ToggleMaintenanceModeCommand = new RelayCommand(_ => IsMaintenanceMode = !IsMaintenanceMode);
        CloseSelectedBlockCommand = new AsyncRelayCommand(_ => CloseSelectedBlockAsync(), _ => CanCloseSelectedBlock());
        ReopenSelectedBlockCommand = new AsyncRelayCommand(_ => ReopenSelectedBlockAsync(), _ => CanReopenSelectedBlock());
        ToggleSelectedBlockSwitchCommand = new AsyncRelayCommand(_ => ToggleSelectedBlockSwitchAsync(), _ => CanToggleSelectedBlockSwitch());
        ApplyTrainPlanCommand = new AsyncRelayCommand(_ => ApplyTrainPlanAsync(), _ => CanApplyTrainPlan());

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
    /// Trains CTC has released on the selected line, with their active movement plan and the
    /// latest speed/authority CTC suggested. Backed by CTC's DispatchedTrains state.
    /// </summary>
    public ObservableCollection<DispatchedTrainViewModel> DispatchedTrains { get; } = new ObservableCollection<DispatchedTrainViewModel>();

    public int DispatchedTrainCount => DispatchedTrains.Count;

    /// <summary>
    /// Dispatched train whose movement plan the dispatcher is editing. Choosing a different
    /// train selects its current stop and loads it into the edit fields.
    /// </summary>
    public DispatchedTrainViewModel? SelectedDispatchedTrain
    {
        get => _selectedDispatchedTrain;
        set
        {
            string? previousTrainId = _selectedDispatchedTrain?.TrainId;
            if (SetProperty(ref _selectedDispatchedTrain, value))
            {
                // A grid rebuild re-selects the same train; keep the dispatcher's unsaved edits then.
                if (!_isRebuildingDispatchedTrains && value?.TrainId != previousTrainId)
                {
                    SelectedStop = value?.Stops.FirstOrDefault();
                    LoadEditedPlan();
                }

                OnPropertyChanged(nameof(HasSelectedDispatchedTrain));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelectedDispatchedTrain => SelectedDispatchedTrain is not null;

    /// <summary>
    /// Stop of <see cref="SelectedDispatchedTrain"/>'s Active Movement Plan being edited (its
    /// current stop by default). Choosing a different stop loads it into the edit fields.
    /// </summary>
    public MovementPlanStopViewModel? SelectedStop
    {
        get => _selectedStop;
        set
        {
            int? previousIndex = _selectedStop?.StopIndex;
            if (SetProperty(ref _selectedStop, value))
            {
                // A refresh re-selects the same stop; keep the dispatcher's unsaved edits then.
                if (!_isRebuildingDispatchedTrains && value is not null && value.StopIndex != previousIndex)
                {
                    LoadEditedPlan();
                }

                OnPropertyChanged(nameof(EditedStopLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Which stop the edit fields apply to, e.g. "Stop 2 (A5)".</summary>
    public string EditedStopLabel => SelectedStop is { } stop ? $"Stop {stop.StopNumber} ({stop.BlockId})" : "Current stop";

    /// <summary>Blocks on the selected line the dispatcher can send a dispatched train to.</summary>
    public ObservableCollection<string> DestinationBlockIds { get; } = new ObservableCollection<string>();

    /// <summary>
    /// Destination chosen for the selected stop; applied only by <see cref="ApplyTrainPlanCommand"/>.
    /// Choosing a different block resets <see cref="EditedDwellTime"/> to that block's default
    /// (45 s at a station, otherwise 0 s), which the dispatcher can then change.
    /// </summary>
    public string? EditedDestinationBlockId
    {
        get => _editedDestinationBlockId;
        set
        {
            if (SetProperty(ref _editedDestinationBlockId, value))
            {
                if (!_isLoadingEditedPlan && SelectedLine is not null && value is not null)
                {
                    EditedDwellTime = DwellTimePolicy.FormatSeconds(DwellTimePolicy.GetDefaultDwellTime(SelectedLine, value));
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// Dwell typed for the selected stop, in whole seconds; validated (not negative) only by
    /// <see cref="ApplyTrainPlanCommand"/>. Blank means the destination's default.
    /// </summary>
    public string EditedDwellTime
    {
        get => _editedDwellTime;
        set => SetProperty(ref _editedDwellTime, value ?? string.Empty);
    }

    /// <summary>
    /// Arrival time typed for <see cref="SelectedDispatchedTrain"/>, as HH:mm:ss; validated
    /// only by <see cref="ApplyTrainPlanCommand"/>.
    /// </summary>
    public string EditedArrivalTime
    {
        get => _editedArrivalTime;
        set => SetProperty(ref _editedArrivalTime, value ?? string.Empty);
    }

    /// <summary>
    /// Sends <see cref="SelectedDispatchedTrain"/>'s edited destination and arrival time to CTC,
    /// which recalculates the route, speed and authority and sends a new movement suggestion.
    /// </summary>
    public ICommand ApplyTrainPlanCommand { get; }

    /// <summary>Tickets sold per hour on the selected line, as last reported by the Track Model.</summary>
    public int CurrentLineTicketSalesPerHour => SelectedLine?.TicketSalesPerHour ?? 0;

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
                RebuildDestinationBlocks();
                RebuildDispatchQueue();
                RebuildDispatchedTrains();
                OnPropertyChanged(nameof(CurrentLineTicketSalesPerHour));
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

    // Confirmed and requested states differ while a request awaits Track Controller
    // confirmation; Close and Reopen are both disabled then, so no contradictory request is sent.
    private bool CanCloseSelectedBlock() =>
        IsMaintenanceMode
        && SelectedBlock is not null
        && SelectedBlock.MaintenanceState == MaintenanceState.Open
        && SelectedBlock.RequestedMaintenanceState != MaintenanceState.Closed;

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

    public ICommand ReopenSelectedBlockCommand { get; }

    private bool CanReopenSelectedBlock() =>
        IsMaintenanceMode
        && SelectedBlock is not null
        && SelectedBlock.MaintenanceState == MaintenanceState.Closed
        && SelectedBlock.RequestedMaintenanceState != MaintenanceState.Open;

    private async Task ReopenSelectedBlockAsync()
    {
        var block = SelectedBlock;
        if (block is null)
        {
            return;
        }

        CommunicationStatus = $"Sending reopen request for Block {block.BlockId}...";

        try
        {
            // The displayed maintenance state stays Closed until the Track Controller
            // reports the block Open in a BlockStatusMessage.
            await _ctc.ReopenBlockAsync(block.BlockId);
            CommunicationStatus = $"Reopen request sent for Block {block.BlockId}.";
        }
        catch (Exception ex)
        {
            // Everything is caught because this runs from an async void command and must
            // never crash the UI.
            CommunicationStatus = $"Unable to send reopen request: {ex.Message}";
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

    // The arrival time is checked when applying, so a bad entry gets an explanation rather
    // than a silently disabled button.
    private bool CanApplyTrainPlan() =>
        SelectedDispatchedTrain is not null
        && !string.IsNullOrWhiteSpace(EditedDestinationBlockId);

    private async Task ApplyTrainPlanAsync()
    {
        var train = SelectedDispatchedTrain;
        string? destinationBlockId = EditedDestinationBlockId;
        if (train is null || string.IsNullOrWhiteSpace(destinationBlockId))
        {
            return;
        }

        if (!ScheduleTemplateConverter.TryParseTime(EditedArrivalTime, out var arrivalTime))
        {
            CommunicationStatus = $"Unable to update {train.TrainDisplayName}: enter the arrival time as {ScheduleTemplateConverter.TimeFormat}.";
            return;
        }

        TimeSpan dwellTime = SelectedLine is null ? TimeSpan.Zero : DwellTimePolicy.GetDefaultDwellTime(SelectedLine, destinationBlockId);
        if (!string.IsNullOrWhiteSpace(EditedDwellTime) && DwellTimePolicy.TryParseSeconds(EditedDwellTime, out dwellTime) is { } dwellError)
        {
            CommunicationStatus = $"Unable to update {train.TrainDisplayName}: {dwellError}";
            return;
        }

        int stopIndex = SelectedStop?.StopIndex ?? 0;
        CommunicationStatus = $"Updating {train.TrainDisplayName}'s movement plan...";

        try
        {
            // Route, timing, speed and authority are all calculated by CTC. On success the status
            // is set from the TrainMovementPlanUpdated state change.
            await _ctc.UpdateMovementPlanStopAsync(train.TrainId, stopIndex, destinationBlockId, arrivalTime, dwellTime);
        }
        catch (Exception ex)
        {
            // CTC kept the train's previous plan. Everything is caught because this runs from
            // an async void command and must never crash the UI.
            CommunicationStatus = $"Unable to update {train.TrainDisplayName}'s movement plan: {ex.Message}";
        }
    }

    /// <summary>Loads the selected stop (or the selected train's current stop) into the edit fields.</summary>
    private void LoadEditedPlan()
    {
        var stop = SelectedStop ?? SelectedDispatchedTrain?.Stops.FirstOrDefault();

        // Loading a stop's own dwell must not be replaced by its destination's default.
        _isLoadingEditedPlan = true;
        try
        {
            EditedDestinationBlockId = stop?.BlockId ?? SelectedDispatchedTrain?.TargetBlockId;
            EditedArrivalTime = stop?.ArrivalTimeDisplay ?? SelectedDispatchedTrain?.TargetArrivalTimeDisplay ?? string.Empty;
            EditedDwellTime = stop?.DwellSecondsDisplay ?? SelectedDispatchedTrain?.TargetDwellSecondsDisplay ?? string.Empty;
        }
        finally
        {
            _isLoadingEditedPlan = false;
        }
    }

    /// <summary>
    /// Re-reads one train after CTC changed it in place, keeping the selected stop. When the
    /// selected stop is gone or now refers to a different block (e.g. the train departed its
    /// current stop), the current stop is selected and loaded instead.
    /// </summary>
    private DispatchedTrainViewModel? RefreshDispatchedTrain(string? trainId, bool reloadEdits)
    {
        var train = DispatchedTrains.FirstOrDefault(train => train.TrainId == trainId);
        if (train is null)
        {
            return null;
        }

        bool isSelected = train == SelectedDispatchedTrain;
        var previousStop = SelectedStop;

        _isRebuildingDispatchedTrains = true;
        try
        {
            train.Refresh();
            if (isSelected)
            {
                SelectedStop = previousStop is null ? null : train.Stops.ElementAtOrDefault(previousStop.StopIndex);
            }
        }
        finally
        {
            _isRebuildingDispatchedTrains = false;
        }

        if (isSelected && (reloadEdits || SelectedStop is null || SelectedStop.BlockId != previousStop?.BlockId))
        {
            SelectedStop ??= train.Stops.FirstOrDefault();
            LoadEditedPlan();
        }

        return train;
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

            case CtcStateChangeKind.TicketSales:
                // CTCService updated the existing CtcLineState in place; just re-read it.
                OnPropertyChanged(nameof(CurrentLineTicketSalesPerHour));
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

            case CtcStateChangeKind.TrainMovementPlanUpdated:
                // The DispatchedTrainState was updated in place; just re-read it.
                var updated = RefreshDispatchedTrain(e.TrainId, reloadEdits: true);

                CommunicationStatus = $"{TrainIds.DisplayName(e.TrainId ?? string.Empty)} movement plan updated"
                    + (updated is null ? string.Empty : $": {updated.TargetBlockId} at {updated.TargetArrivalTimeDisplay}, dwell {updated.TargetDwellSecondsDisplay} s")
                    + " (timing recalculated)."
                    + (e.Message is null ? string.Empty : $" Warning: {e.Message}");
                break;

            case CtcStateChangeKind.TrainLocationUpdated:
                // Only the current block of one train changed; the dispatcher's edits are kept.
                RefreshDispatchedTrain(e.TrainId, reloadEdits: false);
                break;

            case CtcStateChangeKind.TrainMovementPhaseChanged:
                // Arrived, dwelling, departed or completed. Departing removes the current stop, in
                // which case the edit fields move to the new current stop.
                RefreshDispatchedTrain(e.TrainId, reloadEdits: false);
                if (e.Message is not null)
                {
                    CommunicationStatus = e.Message;
                }

                break;

            case CtcStateChangeKind.TrainMovementSuggestionFailed:
                // The train keeps its plan; CTC retries on the next time update.
                CommunicationStatus = $"{e.Message} Will retry on the next time update.";
                break;

            case CtcStateChangeKind.TrainLocationAmbiguous:
                CommunicationStatus = $"Warning: {e.Message}";
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
        var selectedTrainId = SelectedDispatchedTrain?.TrainId;
        var selectedStopIndex = SelectedStop?.StopIndex;

        // Clearing the grid briefly deselects the train; that must not discard the edits.
        _isRebuildingDispatchedTrains = true;
        try
        {
            DispatchedTrains.Clear();

            var lineId = SelectedLine?.LineId;
            foreach (var train in _ctc.State.DispatchedTrains.Where(train => train.LineId == lineId))
            {
                DispatchedTrains.Add(new DispatchedTrainViewModel(train));
            }

            // Keep the dispatcher's selection (and unsaved edits) when the train is still listed.
            SelectedDispatchedTrain = DispatchedTrains.FirstOrDefault(train => train.TrainId == selectedTrainId);
            SelectedStop = selectedStopIndex is { } index ? SelectedDispatchedTrain?.Stops.ElementAtOrDefault(index) : null;
        }
        finally
        {
            _isRebuildingDispatchedTrains = false;
        }

        if (SelectedDispatchedTrain is null)
        {
            LoadEditedPlan();
        }
        else
        {
            SelectedStop ??= SelectedDispatchedTrain.Stops.FirstOrDefault();
        }

        OnPropertyChanged(nameof(DispatchedTrainCount));
    }

    private void RebuildDestinationBlocks()
    {
        DestinationBlockIds.Clear();

        foreach (var block in SelectedLine?.Blocks ?? Enumerable.Empty<CtcBlockState>())
        {
            DestinationBlockIds.Add(block.BlockId);
        }
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
