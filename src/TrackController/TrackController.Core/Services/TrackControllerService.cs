using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>Controller domain: no UI, transport, file access, or references to other modules.</summary>
public sealed partial class TrackControllerService
{
    private sealed class Block(WaysideBlockDefinition definition)
    {
        public WaysideBlockDefinition Definition { get; } = definition;

        public TrackModelBlockStateMessage? Feedback;
        public DateTimeOffset Received;
        public string Maintenance = "Open";
        public SwitchPosition DesiredSwitch = SwitchPosition.Unknown;
        public SwitchPosition CommandedSwitch = SwitchPosition.Unknown;
        public SignalState CommandedSignal = SignalState.Red;
        public CrossingState CommandedCrossing = CrossingState.Closed;
        public bool ManualRed;
        public bool ManualCrossingClosed;
    }

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private WaysideConfigurationMessage _configuration;
    private Dictionary<string, Block> _blocks = [];
    private HashSet<string> _owned = [], _neighbors = [];
    private readonly Dictionary<string, (MovementSuggestionMessage Value, DateTimeOffset Received)> _requests = [];
    private readonly HashSet<string> _retiredRequests = [];
    private VitalPlcProgram? _plc;
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
                _plc = VitalPlcProgram.Compile(source);
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
    public void SetSimulation(bool paused, double speedMultiplier = 1)
    {
        if (!double.IsFinite(speedMultiplier) || speedMultiplier is < 1 or > 10)
        {
            throw new ArgumentException("Simulation speed must be 1–10×.");
        }

        lock (_gate)
        {
            Reevaluate();
            if (!paused && _hardwareMode && _plc is null)
            {
                throw new InvalidOperationException("Load a valid PLC before resuming hardware mode.");
            }

            _paused = paused;
            _multiplier = speedMultiplier;
            Reevaluate();
        }
    }

    /// <summary>Validates the full measurement batch before applying any block updates.</summary>
    public void ApplyFeedback(IReadOnlyList<TrackModelBlockStateMessage> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        lock (_gate)
        {
            if (blocks.Count > _blocks.Count || blocks.Any(feedback => feedback is null) || blocks.Select(feedback => feedback.BlockId).Distinct().Count() != blocks.Count)
            {
                throw new ArgumentException("Feedback must contain distinct owned or observed blocks.");
            }

            var now = _clock.GetUtcNow();
            foreach (var feedback in blocks)
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

            foreach (var feedback in blocks)
            {
                var block = GetBlock(feedback.BlockId);
                if (block.Feedback is not null && feedback.ObservedAtUtc <= block.Feedback.ObservedAtUtc)
                {
                    continue;
                }

                block.Feedback = feedback with { };
                block.Received = now;
                if (feedback.Occupancy == OccupancyState.Clear && _requests.TryGetValue(feedback.BlockId, out var request))
                {
                    _retiredRequests.Add(request.Value.RequestId);
                }

                if (block.Maintenance == "Pending closure" && ClosurePlc(block))
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
            _retiredRequests.Clear();
            _paused = true;
            _maintenanceMode = false;
            _simulationSeconds = 0;
            InitializeBlocks();
            Reevaluate();
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

    private bool IsActive((MovementSuggestionMessage Value, DateTimeOffset Received) request) => (_clock.GetUtcNow() - request.Received).TotalSeconds <= _configuration.RequestTimeoutSeconds;

    private static IEnumerable<string> GetRequestPath((MovementSuggestionMessage Value, DateTimeOffset Received) request) => new[]
        {
        request.Value.TargetBlockId
    }.Concat(request.Value.PathBlockIds);
}
