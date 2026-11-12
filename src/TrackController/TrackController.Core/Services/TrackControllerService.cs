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
        public BlockAuthority? Authority;

        /// <summary>When this switch was first commanded to its current <see cref="CommandedSwitch"/> while the Track Model still reported the other position; null once confirmed.</summary>
        public DateTimeOffset? SwitchThrowStartedAt;

        /// <summary>Approach locking: no throw before this time, because an authority over the protection zone was withdrawn while its train may still be approaching.</summary>
        public DateTimeOffset? SwitchApproachLockedUntil;

        /// <summary>Operator-readable state of this switch's automatic route alignment in the latest evaluation (empty when no route needs it).</summary>
        public string SwitchStatus = string.Empty;
    }

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private WaysideConfigurationMessage _configuration;
    private Dictionary<string, Block> _blocks = [];
    private HashSet<string> _owned = [], _neighbors = [];
    private readonly Dictionary<string, (MovementSuggestionMessage Value, DateTimeOffset Received)> _requests = [];
    private readonly HashSet<string> _retiredRequests = [];

    /// <summary>
    /// Active requests CTC cancelled, by ID, with the route each had. A copy of a cancelled route is never
    /// accepted again (unlike retirement, the train did not move on); the ID may still name a different route.
    /// </summary>
    private readonly Dictionary<string, MovementSuggestionMessage> _cancelledRequests = new(StringComparer.Ordinal);

    /// <summary>
    /// Staged successor requests, by request ID. Kept apart from <see cref="_requests"/>: a staged
    /// successor is never evaluated, takes part in no route-conflict check and commands nothing.
    /// It only becomes a request when <see cref="TransferStagedAuthority"/> promotes it.
    /// </summary>
    private readonly Dictionary<string, (MovementSuggestionMessage Value, DateTimeOffset Received)> _staged = new(StringComparer.Ordinal);

    /// <summary>Every request's final result from the latest evaluation (the grants actually in force).</summary>
    private List<(MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason)> _lastResults = [];
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

    /// <summary>Starts the production lifecycle without exposing a local simulation clock or pause control.</summary>
    public void EnableAutomaticOperation()
    {
        lock (_gate)
        {
            _configuration.AutomaticOperation = true;
            _paused = _hardwareMode && _plc is null;
            _multiplier = 1;
            _simulationSeconds = 0;
            Reevaluate();
        }
    }

    /// <summary>Withdraws all output when the owning runtime shuts down; not an operator time control.</summary>
    public void StopOperation()
    {
        lock (_gate)
        {
            _paused = true;
            _requests.Clear();
            _staged.Clear();
            Reevaluate();
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
            if (_requests.Count > 0 || _staged.Count > 0 || _blocks.Values.Any(block => block.Maintenance != "Open" || (block.Feedback is not null && (!IsFresh(block) || block.Feedback.Occupancy != OccupancyState.Clear))))
            {
                throw new InvalidOperationException("Cancel requests, confirm clear track, and reopen maintenance blocks before replacing the layout.");
            }

            if (_configuration.AutomaticOperation && _plc is not null && !_maintenanceMode)
                throw new InvalidOperationException("Enter maintenance mode before replacing the layout.");
            copy.AutomaticOperation |= _configuration.AutomaticOperation;
            _configuration = copy;
            _paused = true;
            _plc = null;
            _plcName = "No program loaded";
            _plcStatus = "Layout changed. Load a valid PLC program.";
            _retiredRequests.Clear();
            _cancelledRequests.Clear();
            _lastResults = [];
            InitializeBlocks();
            Reevaluate();
        }
    }

    /// <summary>Pauses operation and loads validated Boolean source; an invalid replacement disables the previous program.</summary>
    public void LoadPlc(string name, string source)
    {
        lock (_gate)
        {
            if (_configuration.AutomaticOperation && _plc is not null && !_maintenanceMode)
                throw new InvalidOperationException("Enter maintenance mode before replacing the PLC.");
            _paused = true;
            _plc = null;
            _plcName = "No program loaded";
            try
            {
                _plc = VitalPlcProgram.Compile(source);
                _plcName = name;
                _paused = !_configuration.AutomaticOperation;
                _plcStatus = _configuration.AutomaticOperation ? "Program loaded. Automatic evaluation active." : "Program loaded. Resume explicitly when ready.";
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
            if (_configuration.AutomaticOperation && _plc is not null && !_maintenanceMode)
                throw new InvalidOperationException("Enter maintenance mode before changing controller logic.");
            _hardwareMode = hardware;
            _paused = !_configuration.AutomaticOperation || (hardware && _plc is null);
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
            if (_configuration.AutomaticOperation)
                throw new InvalidOperationException("Simulation time and pause belong to CTC. Use maintenance mode for wayside servicing.");
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
                if (!_owned.Contains(feedback.BlockId) && !_neighbors.Contains(feedback.BlockId) && !(_configuration.CoordinatedRoutes && _blocks.ContainsKey(feedback.BlockId)))
                {
                    throw new ArgumentException($"Block {feedback.BlockId} is neither owned nor an observed neighbor.");
                }

                if (!Enum.IsDefined(feedback.Occupancy) || !Enum.IsDefined(feedback.Signal) || !Enum.IsDefined(feedback.Switch) || !Enum.IsDefined(feedback.Crossing) || feedback.ObservedAtUtc > now.AddSeconds(1) || feedback.ObservedAtUtc == default || (feedback.ActualSpeedMetersPerSecond is double speed && (!double.IsFinite(speed) || speed < 0)))
                {
                    throw new ArgumentException($"Block {feedback.BlockId}: invalid feedback values or timestamp.");
                }
            }

            // The grants in force BEFORE this observation: a staged successor may only take over
            // from a predecessor that actually authorized movement into the successor's target.
            if (_staged.Count > 0)
            {
                Reevaluate();
            }

            var grantsBefore = _lastResults.Where(result => result.AuthorizedAuthorityMeters > 0)
                .ToDictionary(result => result.Request.RequestId, StringComparer.Ordinal);
            var clearBefore = _staged.Count == 0 ? [] : _blocks.Values
                .Where(block => IsFresh(block) && block.Feedback!.Occupancy == OccupancyState.Clear && !block.Feedback.IsFaulted)
                .Select(block => block.Definition.BlockId).ToHashSet(StringComparer.Ordinal);
            var applied = new List<Block>();
            foreach (var feedback in blocks)
            {
                var block = GetBlock(feedback.BlockId);
                if (block.Feedback is not null && feedback.ObservedAtUtc <= block.Feedback.ObservedAtUtc)
                {
                    continue;
                }

                block.Feedback = feedback with { };
                block.Received = now;
                if (feedback.Occupancy == OccupancyState.Clear)
                {
                    block.Authority = null;
                }
                applied.Add(block);
            }

            // One state transition: promotions (with their predecessors' retirement) happen before
            // any other request is retired or re-evaluated, so no evaluation ever sees both or neither.
            TransferStagedAuthority(applied, clearBefore, grantsBefore);

            foreach (var block in applied)
            {
                if (block.Feedback!.Occupancy == OccupancyState.Clear && _requests.TryGetValue(block.Definition.BlockId, out var request))
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
            _staged.Clear();
            _retiredRequests.Clear();
            _cancelledRequests.Clear();
            _lastResults = [];
            _paused = !_configuration.AutomaticOperation || (_hardwareMode && _plc is null);
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
