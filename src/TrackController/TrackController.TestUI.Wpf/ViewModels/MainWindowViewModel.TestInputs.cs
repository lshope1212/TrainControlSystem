using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.TestUI.Wpf.ViewModels;

/// <summary>Organizes the window commands and their input state.</summary>
public sealed partial class MainWindowViewModel
{
    private string _scenario = "Clear route";
    // Form buffers are applied only by their explicit Send / Apply command.
    private string _targetBlock = "5", _trainLabel = "Train 101", _speed = "25", _path = "6, 7, 8";
    private string _entryBlock = "";
    private string _switchTarget = "6", _maintenanceTarget = "12", _feedbackSwitch = "6", _feedbackSignal = "4", _feedbackCrossing = "8";
    private SwitchPosition _requestedSwitch = SwitchPosition.Normal, _reportedSwitch = SwitchPosition.Normal;
    private SignalState _reportedSignal = SignalState.Green;
    private CrossingState _reportedCrossing = CrossingState.Closed;
    private MaintenanceState _requestedMaintenance = MaintenanceState.Closed;

    // WPF temporarily clears SelectedItem while a replacement deployment awaits its first output.
    // Preserve the user's draft selection until the new territory choices arrive.
    public string TargetBlock
    {
        get => _targetBlock;
        set
        {
            if (value is not null)
            {
                if (SetProperty(ref _targetBlock, value))
                {
                    OnPropertyChanged(nameof(RequestedAuthority));
                }
            }
        }
    }

    public string EntryBlock
    {
        get => _entryBlock;
        set => SetProperty(ref _entryBlock, value);
    }

    public string TrainLabel
    {
        get => _trainLabel;
        set => SetProperty(ref _trainLabel, value);
    }

    public string SuggestedSpeed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    public string RequestedAuthority => CalculateDraftAuthority() is double distance ? (distance / DisplayUnits.MetersPerFoot).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : "—";

    public string RequestedPath
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(RequestedAuthority));
            }
        }
    }

    public string SwitchTarget
    {
        get => _switchTarget;
        set
        {
            if (value is not null)
            {
                if (SetProperty(ref _switchTarget, value))
                {
                    OnPropertyChanged(nameof(SwitchFeedbackStatus));
                }
            }
        }
    }

    public string MaintenanceTarget
    {
        get => _maintenanceTarget;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _maintenanceTarget, value);
            }
        }
    }

    public string FeedbackSwitch
    {
        get => _feedbackSwitch;
        set
        {
            if (value is not null && SetProperty(ref _feedbackSwitch, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public string FeedbackSignal
    {
        get => _feedbackSignal;
        set
        {
            if (value is not null && SetProperty(ref _feedbackSignal, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public string FeedbackCrossing
    {
        get => _feedbackCrossing;
        set
        {
            if (value is not null && SetProperty(ref _feedbackCrossing, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public SwitchPosition RequestedSwitch
    {
        get => _requestedSwitch;
        set => SetProperty(ref _requestedSwitch, value);
    }

    public SwitchPosition ReportedSwitch
    {
        get => _reportedSwitch;
        set
        {
            if (SetProperty(ref _reportedSwitch, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public SignalState ReportedSignal
    {
        get => _reportedSignal;
        set
        {
            if (SetProperty(ref _reportedSignal, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public CrossingState ReportedCrossing
    {
        get => _reportedCrossing;
        set
        {
            if (SetProperty(ref _reportedCrossing, value))
            {
                MarkFeedbackDirty();
            }
        }
    }

    public MaintenanceState RequestedMaintenance
    {
        get => _requestedMaintenance;
        set => SetProperty(ref _requestedMaintenance, value);
    }

    public string Scenario
    {
        get => _scenario;
        set
        {
            if (SetProperty(ref _scenario, value))
            {
                OnPropertyChanged(nameof(ScenarioDescription));
            }
        }
    }

    public string[] Scenarios { get; } = ["Clear route", "All blocks clear", "Switch mismatch", "Occupied route", "Track fault", "Crossing awaiting feedback"];

    public Array Occupancies { get; } = Enum.GetValues<OccupancyState>();

    public Array SwitchPositions { get; } = Enum.GetValues<SwitchPosition>();

    public KeyValuePair<SignalState, string>[] SignalStates { get; } = Enum.GetValues<SignalState>().Select(state => new KeyValuePair<SignalState, string>(state, state == SignalState.SuperGreen ? "Super Green" : state.ToString())).ToArray();

    public Array CrossingStates { get; } = Enum.GetValues<CrossingState>();

    public Array MaintenanceStates { get; } = Enum.GetValues<MaintenanceState>();

    private bool _feedbackDirty = true;
    private bool _hasSentFeedback;

    public bool HasUnsentFeedback => _feedbackDirty;

    public string FeedbackDraftStatus => !_hasSentFeedback ? "Draft · no feedback sent yet" : _feedbackDirty ? "Unsent edits · controller still has the last sent feedback" : "Feedback sent · editor matches the last transmission";

    private ScenarioPlan? Plan => ScenarioPlan.From(Blocks);

    public string ScenarioDescription => Plan is not { } plan ? "Presets need a layout with a switch. Edit feedback for your loaded territory directly." : Scenario switch
    {
        "Clear route" => $"Block {plan.Target} occupied; route {string.Join(" → ", plan.Path)} clear, switch normal, signals green, crossing closed.",
        "All blocks clear" => "All blocks clear and unreserved for equipment testing. Send feedback, then resume or enable maintenance mode before requesting a switch change.",
        "Switch mismatch" => $"Switch {plan.SwitchId} reversed. A request through {plan.SwitchId} → {plan.NormalExit} will be withheld.",
        "Occupied route" => $"Block {plan.NormalExit} occupied. Authority stops before the occupied block.",
        "Track fault" => $"Block {plan.NormalExit} reports a fault. The route will be withheld.",
        "Crossing awaiting feedback" => $"Crossing {plan.CrossingId ?? FeedbackCrossing} reports open. The route waits for closed feedback.",
        _ => "Choose a preset for the loaded territory."
    };

    private void MarkFeedbackDirty()
    {
        _feedbackDirty = true;
        OnPropertyChanged(nameof(HasUnsentFeedback));
        OnPropertyChanged(nameof(FeedbackDraftStatus));
    }

    private void FeedbackEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs) => MarkFeedbackDirty();

    private void RefreshDraftSelections()
    {
        static string ValidSelection(string selected, string[] choices) => choices.Length == 0 || choices.Contains(selected) ? selected : choices[0];
        TargetBlock = ValidSelection(TargetBlock, BlockIds);
        MaintenanceTarget = ValidSelection(MaintenanceTarget, BlockIds);
        SwitchTarget = ValidSelection(SwitchTarget, SwitchIds);
        FeedbackSwitch = ValidSelection(FeedbackSwitch, SwitchIds);
        FeedbackSignal = ValidSelection(FeedbackSignal, SignalIds);
        FeedbackCrossing = ValidSelection(FeedbackCrossing, CrossingIds);
        foreach (string property in new[]
        {
            nameof(TargetBlock),
            nameof(MaintenanceTarget),
            nameof(SwitchTarget),
            nameof(FeedbackSwitch),
            nameof(FeedbackSignal),
            nameof(FeedbackCrossing)
        }

        )
        {
            OnPropertyChanged(property);
        }
    }

    private void CreateFeedbackRows()
    {
        foreach (var row in FeedbackRows)
        {
            row.PropertyChanged -= FeedbackEdited;
        }

        FeedbackRows.Clear();
        string occupied = Plan?.Target ?? BlockIds.FirstOrDefault() ?? "";
        foreach (string id in BlockIds)
        {
            var row = new FeedbackRow(id)
            {
                Occupancy = id == occupied ? OccupancyState.Occupied : OccupancyState.Clear
            };
            row.PropertyChanged += FeedbackEdited;
            FeedbackRows.Add(row);
        }

        _hasSentFeedback = false;
        MarkFeedbackDirty();
    }

    private async Task SendMovementAsync()
    {
        var request = new WaysideMovementRequestMessage
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = TrainLabel,
            TargetBlockId = TargetBlock,
            EntryBlockId = string.IsNullOrWhiteSpace(EntryBlock) ? null : EntryBlock.Trim(),
            SuggestedSpeedMetersPerSecond = ParseNumber(SuggestedSpeed, "Suggested speed") * DisplayUnits.MetersPerSecondPerMph,
            SuggestedAuthorityMeters = CalculateDraftAuthority() ?? throw new ArgumentException("Enter a connected route using distinct blocks in this layout to calculate authority."),
            PathBlockIds = ParseRequestedPath()
        };
        await SendAsync(request);
        SetAppliedRequest(request);
        SelectedBlockId = TargetBlock;
        MovementProgress = $"Following the last sent route from block {request.TargetBlockId}. Send feedback as occupancy moves to its next block.";
    }

    private async Task ApplyFeedbackAsync()
    {
        var message = new WaysideFeedbackMessage
        {
            Blocks = FeedbackRows.Select(row => new WaysideBlockFeedback { BlockId = row.BlockId, ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = row.Occupancy, IsFaulted = row.IsFaulted, IsReservedByCtc = row.IsReserved, Signal = row.BlockId == FeedbackSignal ? ReportedSignal : row.Signal, Switch = row.BlockId == FeedbackSwitch ? ReportedSwitch : row.Switch, Crossing = row.BlockId == FeedbackCrossing ? ReportedCrossing : row.Crossing }).ToList()
        };
        await SendAsync(message);
        _appliedFeedback = message;
        _hasSentFeedback = true;
        _feedbackDirty = false;
        OnPropertyChanged(nameof(HasUnsentFeedback));
        OnPropertyChanged(nameof(FeedbackDraftStatus));
        Status = ShouldRepeatInputs ? "Track feedback sent. Repeating the sent values every second." : "Track feedback sent once. It expires after the configured timeout (5 seconds by default).";
        await FollowMovementAsync(message);
    }

    private async Task ApplyLatchedFeedbackAsync()
    {
        foreach (var feedback in _appliedFeedback!.Blocks)
        {
            feedback.ObservedAtUtc = DateTimeOffset.UtcNow;
        }

        await SendAsync(_appliedFeedback, announce: false);
    }

    /// <summary>Points the drafts at the switch route of a newly reported layout.</summary>
    private void ApplyLayoutDefaults()
    {
        EntryBlock = "";
        if (Plan is not { } plan)
        {
            return;
        }

        TargetBlock = plan.Target;
        RequestedPath = string.Join(", ", plan.Path);
        SwitchTarget = plan.SwitchId;
        FeedbackSwitch = plan.SwitchId;
        FeedbackSignal = plan.SignalId ?? FeedbackSignal;
        FeedbackCrossing = plan.CrossingId ?? FeedbackCrossing;
        MaintenanceTarget = plan.LastBlockId;
    }

    private void LoadScenario()
    {
        var plan = Plan ?? throw new InvalidOperationException("These presets need a layout with a switch. Edit feedback for your loaded territory directly.");
        FeedbackSwitch = plan.SwitchId;
        FeedbackSignal = plan.SignalId ?? FeedbackSignal;
        FeedbackCrossing = plan.CrossingId ?? FeedbackCrossing;
        foreach (var row in FeedbackRows)
        {
            row.Occupancy = Scenario != "All blocks clear" && row.BlockId == plan.Target ? OccupancyState.Occupied : OccupancyState.Clear;
            row.IsFaulted = false;
            row.IsReserved = false;
            row.Signal = SignalState.Green;
            row.Switch = SwitchPosition.Normal;
            row.Crossing = CrossingState.Closed;
            if (Scenario == "Switch mismatch" && plan.MismatchOccupied.Contains(row.BlockId))
            {
                row.Occupancy = OccupancyState.Occupied;
            }

            if (Scenario == "Occupied route" && row.BlockId == plan.NormalExit)
            {
                row.Occupancy = OccupancyState.Occupied;
            }

            if (Scenario == "Track fault" && row.BlockId == plan.NormalExit)
            {
                row.IsFaulted = true;
            }
        }

        ReportedSwitch = Scenario == "Switch mismatch" ? SwitchPosition.Reverse : SwitchPosition.Normal;
        ReportedSignal = SignalState.Green;
        ReportedCrossing = Scenario == "Crossing awaiting feedback" ? CrossingState.Open : CrossingState.Closed;
        MarkFeedbackDirty();
        Status = $"{Scenario} loaded into the editor. Review it, then select Send feedback.";
    }

}
