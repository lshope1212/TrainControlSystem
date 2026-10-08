using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Interfaces;
using TrackController.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Interfaces;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>Controller domain: no UI, transport, file access, or references to other modules.</summary>
public sealed partial class TrackControllerService : ITrackControllerService, ITrackController
{
    private sealed class Block(WaysideBlockDefinition definition)
    {
        public WaysideBlockDefinition Definition { get; } = definition;

        public WaysideBlockFeedback? Feedback;
        public DateTimeOffset Received;
        public string Maintenance = "Open";
        public SwitchPosition DesiredSwitch = SwitchPosition.Unknown;
        public SwitchPosition CommandedSwitch = SwitchPosition.Unknown;
        public SignalState CommandedSignal = SignalState.Red;
        public CrossingState CommandedCrossing = CrossingState.Closed;
        public bool ManualRed;
        public bool ManualCrossingClosed;
    }

    private sealed record Request(WaysideMovementRequestMessage Value, DateTimeOffset Received);

    private sealed record RequestOrigin(string TargetBlockId, string? EntryBlockId, double FrontOffsetMeters, string[] PathBlockIds)
    {
        /// <summary>Checks that a repeated request retains its original front position and ordered route.</summary>
        public bool Matches(WaysideMovementRequestMessage request) => TargetBlockId == request.TargetBlockId && EntryBlockId == request.EntryBlockId && FrontOffsetMeters == request.FrontOffsetMeters && PathBlockIds.SequenceEqual(request.PathBlockIds);
    }

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private WaysideConfigurationMessage _configuration;
    private Dictionary<string, Block> _blocks = [];
    private HashSet<string> _owned = [], _neighbors = [];
    private readonly Dictionary<string, Request> _requests = [];
    private readonly Dictionary<string, RequestOrigin> _requestOrigins = [];
    private readonly HashSet<string> _retiredRequests = [];
    private BooleanPlcProgram? _plc;
    private string _plcName = "No program loaded";
    private string _plcStatus = "Load a Boolean PLC program to enable hardware mode.";
    private bool _paused = true;
    private bool _hardwareMode = true;
    private bool _maintenanceMode;
    private double _multiplier = 1;
    private double _simulationSeconds;
    private DateTimeOffset _lastPulse;
    private WaysideSnapshotMessage _snapshot = null!;

    /// <summary>Creates a paused controller with validated topology and an injectable watchdog clock.</summary>
    public TrackControllerService(WaysideConfigurationMessage? configuration = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _configuration = WaysideConfiguration.ValidateAndCopy(configuration ?? WaysideConfiguration.CreateDemo());
        _lastPulse = _clock.GetUtcNow();
        InitializeBlocks();
        Reevaluate();
    }

    public ControllerState State
    {
        get
        {
            lock (_gate)
            {
                Reevaluate();
                return new ControllerState
                {
                    WaysideId = _configuration.WaysideId,
                    CommandedSignalState = _snapshot.Blocks.FirstOrDefault(blockStatus => blockStatus.HasSignal)?.CommandedSignal ?? SignalState.Red,
                    CommandedSwitchPosition = _snapshot.Blocks.FirstOrDefault(blockStatus => blockStatus.HasSwitch)?.CommandedSwitch ?? SwitchPosition.Unknown,
                    IsCrossingActive = _snapshot.Blocks.Any(blockStatus => blockStatus.HasCrossing && blockStatus.CommandedCrossing == CrossingState.Closed)
                };
            }
        }
    }

    public ControllerConfiguration Configuration
    {
        get
        {
            lock (_gate)
            {
                var result = new ControllerConfiguration
                {
                    WaysideId = _configuration.WaysideId
                };
                foreach (string id in _owned)
                {
                    result.OwnedBlockIds.Add(id);
                }

                return result;
            }
        }
    }

    /// <summary>Returns a defensive copy so callers cannot alter the configured topology.</summary>
    public WaysideConfigurationMessage GetConfiguration()
    {
        lock (_gate)
        {
            return WaysideConfiguration.ValidateAndCopy(_configuration);
        }
    }

    /// <summary>Reevaluates current inputs and returns outputs with a bounded validity interval.</summary>
    public WaysideSnapshotMessage GetSnapshot()
    {
        lock (_gate)
        {
            Reevaluate();
            return _snapshot;
        }
    }

    /// <summary>Replaces a validated layout only when the existing territory can be safely reset.</summary>
    public void Configure(WaysideConfigurationMessage configuration)
    {
        var copy = WaysideConfiguration.ValidateAndCopy(configuration);
        lock (_gate)
        {
            if (_requests.Count > 0 || _blocks.Values.Any(block => block.Maintenance != "Open" || (block.Feedback is not null && (!IsFresh(block) || block.Feedback.Occupancy != OccupancyState.Clear))))
            {
                throw new InvalidOperationException("Cancel requests, confirm clear track, and reopen maintenance blocks before replacing the layout.");
            }

            _configuration = copy;
            _paused = true;
            _plc = null;
            _plcName = "No program loaded";
            _plcStatus = "Layout changed. Load a PLC program and resume explicitly.";
            _requestOrigins.Clear();
            _retiredRequests.Clear();
            InitializeBlocks();
            Reevaluate();
        }
    }

    /// <summary>Pauses operation and loads validated Boolean source; an invalid replacement disables the previous program.</summary>
    public void LoadPlc(string name, string source)
    {
        lock (_gate)
        {
            _paused = true;
            _plc = null;
            _plcName = "No program loaded";
            try
            {
                _plc = BooleanPlcProgram.Compile(source);
                _plcName = name;
                _plcStatus = "Program loaded. Resume explicitly when ready.";
            }
            catch (ArgumentException ex)
            {
                _plcStatus = ex.Message;
                Reevaluate();
                throw;
            }

            Reevaluate();
        }
    }

    /// <summary>Selects the local evaluation strategy and pauses before the new strategy can authorize movement.</summary>
    public void SetHardwareMode(bool hardware)
    {
        lock (_gate)
        {
            _hardwareMode = hardware;
            _paused = true;
            Reevaluate();
        }
    }

    /// <summary>Controls maintenance access and withholds automatic movement while it is enabled.</summary>
    public void SetMaintenanceMode(bool enabled)
    {
        lock (_gate)
        {
            _maintenanceMode = enabled;
            if (!enabled)
            {
                foreach (var block in _blocks.Values)
                {
                    block.ManualRed = false;
                    block.ManualCrossingClosed = false;
                }
            }

            Reevaluate();
        }
    }

    /// <summary>Applies pause and simulation speed settings while preserving wall-clock feedback timeouts.</summary>
    public void SetSimulation(WaysideSimulationMessage message)
    {
        if (!double.IsFinite(message.SpeedMultiplier) || message.SpeedMultiplier is < 1 or > 10)
        {
            throw new ArgumentException("Simulation speed must be 1–10×.");
        }

        lock (_gate)
        {
            Reevaluate();
            if (!message.IsPaused && _hardwareMode && _plc is null)
            {
                throw new InvalidOperationException("Load a valid PLC before resuming hardware mode.");
            }

            _paused = message.IsPaused;
            _multiplier = message.SpeedMultiplier;
            Reevaluate();
        }
    }

    /// <summary>Validates the full measurement batch before applying any block updates.</summary>
    public void ApplyFeedback(WaysideFeedbackMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (message.Blocks is null || message.Blocks.Count > _blocks.Count || message.Blocks.Any(feedback => feedback is null) || message.Blocks.Select(feedback => feedback.BlockId).Distinct().Count() != message.Blocks.Count)
            {
                throw new ArgumentException("Feedback must contain distinct owned or observed blocks.");
            }

            var now = _clock.GetUtcNow();
            foreach (var feedback in message.Blocks)
            {
                if (!_owned.Contains(feedback.BlockId) && !_neighbors.Contains(feedback.BlockId))
                {
                    throw new ArgumentException($"Block {feedback.BlockId} is neither owned nor an observed neighbor.");
                }

                if (!Enum.IsDefined(feedback.Occupancy) || !Enum.IsDefined(feedback.Signal) || !Enum.IsDefined(feedback.Switch) || !Enum.IsDefined(feedback.Crossing) || feedback.ObservedAtUtc > now.AddSeconds(1) || feedback.ObservedAtUtc == default || (feedback.ActualSpeedMetersPerSecond is double speed && (!double.IsFinite(speed) || speed < 0)))
                {
                    throw new ArgumentException($"Block {feedback.BlockId}: invalid feedback values or timestamp.");
                }
            }

            foreach (var feedback in message.Blocks)
            {
                var block = GetBlock(feedback.BlockId);
                if (block.Feedback is not null && feedback.ObservedAtUtc <= block.Feedback.ObservedAtUtc)
                {
                    continue;
                }

                block.Feedback = new WaysideBlockFeedback
                {
                    BlockId = feedback.BlockId,
                    ObservedAtUtc = feedback.ObservedAtUtc,
                    Occupancy = feedback.Occupancy,
                    Signal = feedback.Signal,
                    Switch = feedback.Switch,
                    Crossing = feedback.Crossing,
                    IsFaulted = feedback.IsFaulted,
                    IsReservedByCtc = feedback.IsReservedByCtc,
                    ActualSpeedMetersPerSecond = feedback.ActualSpeedMetersPerSecond
                };
                block.Received = now;
                if (feedback.Occupancy == OccupancyState.Clear && _requests.TryGetValue(feedback.BlockId, out var request))
                {
                    _retiredRequests.Add(request.Value.RequestId);
                }

                if (block.Maintenance == "Pending closure" && IsFresh(block) && feedback.Occupancy == OccupancyState.Clear)
                {
                    block.Maintenance = "Closed";
                }
            }

            Reevaluate();
        }
    }

    /// <summary>Only the isolated test host should call this; live transport never exposes reset.</summary>
    public void ResetTestState()
    {
        lock (_gate)
        {
            _requests.Clear();
            _requestOrigins.Clear();
            _retiredRequests.Clear();
            _paused = true;
            _maintenanceMode = false;
            _simulationSeconds = 0;
            InitializeBlocks();
            Reevaluate();
        }
    }

    /// <summary>Maps an existing team Track State message into the controller feedback boundary.</summary>
    public void ApplyTrackState(TrackStateMessage state) => ApplyFeedback(new WaysideFeedbackMessage { Blocks = [new WaysideBlockFeedback { BlockId = state.BlockId, ObservedAtUtc = _clock.GetUtcNow(), Occupancy = state.IsOccupied ? OccupancyState.Occupied : OccupancyState.Clear, Signal = state.Signal, Switch = state.Switch }] });

    /// <summary>Returns measured state for the requested block using the existing shared contract.</summary>
    public TrackStateMessage GetTrackState(string blockId)
    {
        lock (_gate)
        {
            Reevaluate();
            var block = GetBlock(blockId);
            return new TrackStateMessage
            {
                BlockId = blockId,
                IsOccupied = block.Feedback?.Occupancy == OccupancyState.Occupied,
                Signal = block.Feedback?.Signal ?? SignalState.Unknown,
                Switch = block.Feedback?.Switch ?? SwitchPosition.Unknown
            };
        }
    }

    private void InitializeBlocks()
    {
        _blocks = _configuration.Blocks.ToDictionary(blockDefinition => blockDefinition.BlockId, blockDefinition => new Block(blockDefinition), StringComparer.Ordinal);
        _owned = WaysideConfiguration.GetOwnedIds(_configuration);
        _neighbors = WaysideConfiguration.GetNeighborIds(_configuration);
    }

    private Block GetBlock(string id) => id is not null && _blocks.TryGetValue(id, out var block) ? block : throw new ArgumentException($"Block '{id}' is outside this territory.");

    private Block GetOwnedBlock(string id) => id is not null && _owned.Contains(id) ? GetBlock(id) : throw new ArgumentException($"Block '{id}' is not owned by {_configuration.WaysideId}; neighboring feedback does not grant control.");

    private bool IsFresh(Block block) => block.Feedback is not null && (_clock.GetUtcNow() - block.Received).TotalSeconds <= _configuration.FeedbackTimeoutSeconds && (_clock.GetUtcNow() - block.Feedback.ObservedAtUtc).TotalSeconds <= _configuration.FeedbackTimeoutSeconds;

    private bool IsActive(Request request) => (_clock.GetUtcNow() - request.Received).TotalSeconds <= _configuration.RequestTimeoutSeconds;

    private static IEnumerable<string> GetRequestPath(Request request) => new[]
        {
        request.Value.TargetBlockId
    }.Concat(request.Value.PathBlockIds);
}
