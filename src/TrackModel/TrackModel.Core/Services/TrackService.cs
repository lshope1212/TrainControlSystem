using TrackModel.Core.Interfaces;
using TrackModel.Core.Models;
using TrackModel.Core.Persistence;
using TrainControl.Common.Identity;
using TrainControl.Common.Utilities;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
namespace TrackModel.Core.Services;

/// <summary>Owns track state. The host serializes mutations. Train position comes
/// from telemetry; this service does not simulate train physics.</summary>
/// <remarks>
/// A train is on the track either by block (<see cref="ApplyTrainUpdate"/>: the development harness and
/// TestUIs place it in one block) or by position (<see cref="RequestTrainPlacement"/> then
/// <see cref="ApplyTrainPosition"/>: a dispatched train whose footprint follows the Train Model's cumulative
/// position and may span several blocks). Either way exactly one block per train is its head
/// (<see cref="TrackBlock.IsTrainHead"/>), and only the head carries the train's environment.
/// </remarks>
public partial class TrackService : ITrackService
{
    private readonly HashSet<(string TrainId, string ExchangeId)> _exchanges = [];
    private readonly Queue<(TimeSpan Time, string LineId, int Count)> _sales = [];
    public TrackLayout Layout { get; private set; } = new();
    public TimeSpan SystemTime { get; private set; }
    public int LayoutRevision { get; private set; }
    public event EventHandler? StateChanged;
    public TrackBlock? FindBlock(string blockId) => Layout.Blocks.FirstOrDefault(b => b.Id == blockId);

    public void LoadLayout(TrackLayout layout)
    {
        TrackLayoutValidator.Validate(layout); // Validate before replacing live state.
        // Tracked trains' routes and footprints refer to the old layout: they leave the track with it.
        ForgetTrackedTrains("the track layout was replaced");
        foreach (var b in layout.Blocks)
        {
            b.IsOccupied = b.IsClosed = b.IsTrainHead = false;
            b.TrainId = string.Empty;
            b.ActualSpeedMetersPerSecond = b.CommandedSpeedMetersPerSecond = b.AuthorityMeters = 0;
            b.MovementRequestId = b.AuthorityEntryBlockId = b.TrainEntryBlockId = string.Empty;
            b.CommandSequence = 0;
            b.AuthorityExpiresAtUtc = null;
            b.Switch = SwitchPosition.Normal; b.Signal = SignalState.Red; b.Crossing = CrossingState.Open;
            b.BrokenRail = b.TrackCircuitFailure = b.PowerFailure = false;
            b.WaitingPassengers = b.InitialWaitingPassengers;
            b.BoardingPassengers = b.DisembarkingPassengers = b.TicketsSold = 0;
        }
        Layout = layout;
        LayoutRevision++;
        _exchanges.Clear();
        _sales.Clear();
        Changed();
    }

    public void ApplyCommand(TrackModelCommandMessage message)
    {
        ApplyCommandCore(message);
        Changed();
    }

    /// <summary>
    /// Applies a wayside batch: each command is validated and applied on its own, in order, exactly
    /// as <see cref="ApplyCommand"/> would, so one refused command (e.g. throwing an occupied switch)
    /// does not hold back the rest. State listeners are notified once, and only if something changed.
    /// </summary>
    /// <returns>One "block: reason" entry per refused command; empty when all were applied.</returns>
    public IReadOnlyList<string> ApplyCommands(IEnumerable<TrackModelCommandMessage> messages) =>
        ApplyCommandBatch(new TrackModelCommandBatchMessage { Commands = (messages ?? throw new ArgumentNullException(nameof(messages))).ToList() }, DateTimeOffset.UtcNow);

    /// <summary>
    /// Applies a wayside batch like <see cref="ApplyCommands"/>, honouring its ordering and lease: a
    /// block's command is skipped (not refused) when the block was already commanded by a batch with
    /// the same or a newer <see cref="TrackModelCommandBatchMessage.Sequence"/>, so a late or replayed
    /// batch can never restore withdrawn authority; and speed/authority codes lapse to zero
    /// <see cref="TrackModelCommandBatchMessage.AuthorityLeaseSeconds"/> after
    /// <paramref name="receivedAtUtc"/> unless a newer batch renews them (see <see cref="ExpireCommands"/>).
    /// </summary>
    public IReadOnlyList<string> ApplyCommandBatch(TrackModelCommandBatchMessage batch, DateTimeOffset receivedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Sequence < 0 || !double.IsFinite(batch.AuthorityLeaseSeconds) || batch.AuthorityLeaseSeconds < 0)
            throw new ArgumentException("A command batch needs a nonnegative sequence and authority lease.");
        DateTimeOffset? expiresAt = batch.AuthorityLeaseSeconds > 0 ? receivedAtUtc.AddSeconds(batch.AuthorityLeaseSeconds) : null;
        var refused = new List<string>();
        var changed = false;
        foreach (var message in batch.Commands ?? [])
        {
            try { changed |= ApplyCommandCore(message, batch.Sequence, expiresAt); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                refused.Add($"{message?.BlockId}: {ex.Message}");
            }
        }
        if (changed) Changed();
        return refused;
    }

    /// <summary>
    /// Replaces every speed/authority code whose lease lapsed with zero: the wayside stopped renewing
    /// it, so it no longer authorizes anything. Returns how many blocks were stopped.
    /// </summary>
    public int ExpireCommands(DateTimeOffset now)
    {
        var expired = 0;
        foreach (var b in Layout.Blocks.Where(b => b.AuthorityExpiresAtUtc is { } at && at <= now))
        {
            b.CommandedSpeedMetersPerSecond = b.AuthorityMeters = 0;
            b.MovementRequestId = b.AuthorityEntryBlockId = string.Empty;
            b.AuthorityExpiresAtUtc = null;
            expired++;
        }
        if (expired > 0) Changed();
        return expired;
    }

    /// <summary>
    /// Where each train's FRONT is and the speed/authority code it receives now, by train ID: exactly one
    /// entry per train, from its head block. A train spanning several blocks is still one train; its
    /// trailing blocks are occupied but carry none of its codes.
    /// </summary>
    public IReadOnlyDictionary<string, (string BlockId, double Speed, double Authority, string RequestId)> CaptureTrainCodes() =>
        Layout.Blocks.Where(b => b.IsOccupied && b.IsTrainHead && b.TrainId.Length > 0)
            .GroupBy(b => b.TrainId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1
                ? (g.Single().Id, g.Single().TrainCommandedSpeedMetersPerSecond, g.Single().TrainAuthorityMeters, g.Single().CodeAppliesToOccupant ? g.Single().MovementRequestId : string.Empty)
                : (string.Empty, 0d, 0d, string.Empty), // never happens; two heads would be an internal error, so the train gets a stop code
                StringComparer.Ordinal);

    /// <summary>Validates, then applies one command; true when the block's state changed.</summary>
    private bool ApplyCommandCore(TrackModelCommandMessage message, long sequence = 0, DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var b = RequireBlock(message.BlockId);
        Nonnegative(message.CommandedSpeedMetersPerSecond, "Commanded speed");
        Nonnegative(message.AuthorityMeters, "Authority");
        if (!Enum.IsDefined(message.Signal) || !Enum.IsDefined(message.Switch) || !Enum.IsDefined(message.Crossing)
            || message.Signal == SignalState.Unknown || message.Switch == SwitchPosition.Unknown || message.Crossing == CrossingState.Unknown)
            throw new ArgumentException("Choose a specific signal, switch, and crossing command.");
        var requestId = message.MovementRequestId ?? string.Empty;
        var entry = message.AuthorityEntryBlockId ?? string.Empty;
        if (requestId.Length > 128 || (entry.Length > 0 && (entry == b.Id || FindBlock(entry) is null)))
            throw new ArgumentException("A movement code needs a request ID of up to 128 characters and a known, different entry block.");
        // Ordering: this block already has a newer wayside command. Nothing from an older batch applies.
        if (sequence > 0 && sequence <= b.CommandSequence) return false;
        if (b.HasSwitch && b.IsOccupied && b.Switch != message.Switch)
            throw new InvalidOperationException("An occupied switch cannot be thrown.");
        // The switch machine is powered from its block: without power the points cannot move.
        if (b.HasSwitch && b.PowerFailure && b.Switch != message.Switch)
            throw new InvalidOperationException("A switch without power cannot be thrown.");
        var changed = b.CommandedSpeedMetersPerSecond != message.CommandedSpeedMetersPerSecond || b.AuthorityMeters != message.AuthorityMeters
            || b.MovementRequestId != requestId || b.AuthorityEntryBlockId != entry
            || (b.HasSwitch && b.Switch != message.Switch) || (b.HasSignal && b.Signal != message.Signal) || (b.HasCrossing && b.Crossing != message.Crossing);
        b.CommandedSpeedMetersPerSecond = message.CommandedSpeedMetersPerSecond;
        b.AuthorityMeters = message.AuthorityMeters;
        b.MovementRequestId = requestId;
        b.AuthorityEntryBlockId = entry;
        if (sequence > 0) b.CommandSequence = sequence;
        b.AuthorityExpiresAtUtc = message.CommandedSpeedMetersPerSecond > 0 || message.AuthorityMeters > 0 ? expiresAt : null;
        if (b.HasSwitch) b.Switch = message.Switch;
        if (b.HasSignal) b.Signal = message.Signal;
        if (b.HasCrossing) b.Crossing = message.Crossing;
        return changed;
    }

    /// <summary>
    /// Places, moves or removes a train by block (the development harness and the TestUIs), and applies a
    /// passenger exchange at the train's current block. An empty CurrentBlockId removes the train.
    /// </summary>
    /// <remarks>
    /// A train tracked from Train Model position telemetry is never moved by block: an update naming its
    /// head block only applies the exchange, an empty block retires it (clearing its whole footprint), and
    /// any other block is refused. A manual placement of a train whose dispatch is still waiting for its
    /// initial report takes over from that pending placement.
    /// </remarks>
    public void ApplyTrainUpdate(TrackModelTrainUpdateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.TrainId)) throw new ArgumentException("Train ID is required.");
        Nonnegative(message.ActualSpeedMetersPerSecond, "Actual speed");
        if (message.BoardingPassengers < 0 || message.DisembarkingPassengers < 0)
            throw new ArgumentException("Passenger counts cannot be negative.");
        var exchangeKey = (message.TrainId, message.ExchangeId);
        var hasExchange = message.BoardingPassengers != 0 || message.DisembarkingPassengers != 0;
        var applyExchange = hasExchange && !_exchanges.Contains(exchangeKey);
        TrackBlock? destination = string.IsNullOrWhiteSpace(message.CurrentBlockId) ? null : RequireBlock(message.CurrentBlockId);

        string trainId = message.TrainId;
        TrackBlock? previous;
        if (TrainIdConvention.TryNormalize(message.TrainId, out var canonical) && _trains.TryGetValue(canonical, out var tracked)
            && tracked.Status != TrainTrackingStatus.PendingPlacement)
        {
            if (destination is null)
            {
                if (hasExchange) throw new ArgumentException("Passenger exchange requires a station block.");
                RetireTrain(canonical, "removed from the track by a train update with no block");
                return;
            }

            if (destination.Id != tracked.HeadBlockId)
                throw new InvalidOperationException($"Train {canonical} is tracked from Train Model position telemetry (front in {tracked.HeadBlockId}); a block move to {destination.Id} is not accepted. Remove the train first.");
            trainId = canonical;
            previous = destination; // an exchange at its head block: no occupancy change
        }
        else
        {
            previous = Layout.Blocks.FirstOrDefault(b => b.TrainId == message.TrainId);
        }

        if (destination is not null && destination.IsOccupied && destination.TrainId != trainId)
            throw new InvalidOperationException($"Block {destination.Id} is occupied by train {destination.TrainId}.");
        if (destination is not null && destination.IsClosed && previous != destination)
            throw new InvalidOperationException("A train cannot enter a block closed for maintenance.");
        if (hasExchange && string.IsNullOrWhiteSpace(message.ExchangeId))
            throw new ArgumentException("Passenger exchanges require an ExchangeId.");
        if (applyExchange)
        {
            if (destination is null || string.IsNullOrWhiteSpace(destination.StationName))
                throw new ArgumentException("Passenger exchange requires a station block.");
            if (message.ActualSpeedMetersPerSecond != 0)
                throw new ArgumentException("Passenger exchange requires a stopped train.");
            if (message.BoardingPassengers > destination.WaitingPassengers)
                throw new ArgumentException("Boarding exceeds the passengers waiting at this station.");
            if (message.BoardingPassengers > int.MaxValue - destination.BoardingPassengers
                || message.DisembarkingPassengers > int.MaxValue - destination.DisembarkingPassengers)
                throw new ArgumentException("Passenger totals are too large.");
        }

        // A manual block placement of a train whose dispatch has not reached the track takes over from it.
        if (TrainIdConvention.TryNormalize(message.TrainId, out var pendingId) && _trains.TryGetValue(pendingId, out var pending)
            && pending.Status == TrainTrackingStatus.PendingPlacement)
        {
            _trains.Remove(pendingId);
            Raise(TrainTrackingEventKind.PlacementWithdrawn, pending, destination is null
                ? "withdrawn by a train update with no block"
                : $"superseded by a manual block placement at {destination.Id}");
        }

        // Validation precedes mutation: a rejected move leaves occupancy intact.
        bool moved = previous != destination;
        if (previous is not null && moved)
        {
            ClearBlock(previous);
        }
        if (destination is not null)
        {
            // The physical boundary the train crossed; a continuation code applies only across it.
            if (moved) destination.TrainEntryBlockId = previous?.Id ?? string.Empty;
            destination.TrainId = trainId; destination.IsOccupied = true; destination.IsTrainHead = true;
            destination.ActualSpeedMetersPerSecond = message.ActualSpeedMetersPerSecond;
            if (applyExchange)
            {
                destination.WaitingPassengers -= message.BoardingPassengers;
                destination.BoardingPassengers += message.BoardingPassengers;
                destination.DisembarkingPassengers += message.DisembarkingPassengers;
                _exchanges.Add(exchangeKey);
            }
        }
        if (moved && (previous ?? destination) is { } changedBlock)
        {
            OccupancyTransition?.Invoke(this, new(trainId, changedBlock.LineId,
                destination is null ? [] : [destination.Id], previous is null ? [] : [previous.Id], destination?.Id ?? string.Empty));
        }
        Changed();
    }

    public void ApplyFailures(TrackModelFailureCommandMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var b = RequireBlock(message.BlockId);
        b.BrokenRail = message.BrokenRail; b.TrackCircuitFailure = message.TrackCircuitFailure; b.PowerFailure = message.PowerFailure;
        Changed();
    }

    public void ApplyTemperature(TrackModelTemperatureCommandMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var block = RequireBlock(message.BlockId);
        if (!double.IsFinite(message.TemperatureCelsius) || message.TemperatureCelsius < -273.15)
            throw new ArgumentException("Temperature must be finite and at least absolute zero (-459.67 °F).");
        block.TemperatureCelsius = message.TemperatureCelsius;
        Changed();
    }

    public void SetSystemTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) throw new ArgumentException("System time cannot be negative.");
        if (time < SystemTime) _sales.Clear();
        SystemTime = time;
        while (_sales.TryPeek(out var sale) && sale.Time <= time - TimeSpan.FromHours(1)) _sales.Dequeue();
        Changed();
    }

    public void ApplyPassengerDemand(TrackModelPassengerDemandMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var block = RequireBlock(message.BlockId);
        if (string.IsNullOrWhiteSpace(block.StationName)) throw new ArgumentException("Passenger demand requires a station block.");
        if (message.WaitingPassengers < 0) throw new ArgumentException("Waiting passengers cannot be negative.");
        block.WaitingPassengers = message.WaitingPassengers;
        Changed();
    }

    public void SetMaintenance(string blockId, MaintenanceState state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentException("Invalid maintenance state.");
        var b = RequireBlock(blockId);
        if (state == MaintenanceState.Closed && b.IsOccupied)
            throw new InvalidOperationException("An occupied block cannot be closed for maintenance.");
        b.IsClosed = state == MaintenanceState.Closed;
        Changed();
    }

    public TrackLayoutMessage CreateLayoutMessage() => new()
    {
        Lines = Layout.Blocks.GroupBy(b => b.LineId).Select(line => new TrackLineDefinition
        {
            LineId = line.Key, Name = line.Key + " Line",
            Blocks = line.Select(b => new TrackBlockDefinition
            {
                BlockId = b.Id, BlockNumber = b.Number, Section = b.Section, LengthMeters = b.LengthMeters,
                StationName = b.StationName, HasSwitch = b.HasSwitch, HasSignal = b.HasSignal,
                HasCrossing = b.HasCrossing, ConnectedBlockIds = [.. b.ConnectedBlockIds],
                ElevationMeters = b.ElevationMeters, GradePercent = b.GradePercent,
                SpeedLimitMetersPerSecond = b.SpeedLimitMetersPerSecond, TravelDirection = b.TravelDirection,
                // CTC plans speeds from the contract's km/h field; both describe the same civil limit.
                SpeedLimitKilometersPerHour = UnitConversion.MetersPerSecondToKilometersPerHour(b.SpeedLimitMetersPerSecond),
                HasHeater = b.HasHeater, Beacon = b.Beacon, BeaconTargetBlockId = b.BeaconTargetBlockId,
                NormalNextBlockId = b.NormalNextBlockId, ReverseNextBlockId = b.ReverseNextBlockId,
                IsYardEntry = b.IsYardEntry
            }).ToList()
        }).ToList()
    };

    public TrackModelBlockStateMessage CreateBlockState(string blockId)
    {
        var b = RequireBlock(blockId);
        return new() { BlockId = b.Id, Occupancy = b.ReportedOccupancy, BrokenRail = b.BrokenRail,
            TrackCircuitFailure = b.TrackCircuitFailure, PowerFailure = b.PowerFailure, IsClosed = b.IsClosed,
            Switch = b.HasSwitch ? b.Switch : SwitchPosition.Unknown,
            Signal = b.HasSignal ? b.EffectiveSignal : SignalState.Unknown,
            Crossing = b.HasCrossing ? b.Crossing : CrossingState.Unknown };
    }

    public TrackModelTrainEnvironmentMessage CreateTrainEnvironment(string blockId)
    {
        var b = RequireBlock(blockId);
        // An occupied block reports the code that applies to ITS train (zero for a continuation code
        // the train did not enter under); an unoccupied block reports its code as commanded.
        // A trailing (non-head) block reports its code without a train, so it is never picked up.
        // Only the head block names its train: a train spanning several blocks receives ONE environment.
        return new() { BlockId = b.Id, TrainId = b.IsTrainHead ? b.TrainId : string.Empty,
            CommandedSpeedMetersPerSecond = b.TrainCommandedSpeedMetersPerSecond, ActualSpeedMetersPerSecond = b.ActualSpeedMetersPerSecond,
            AuthorityMeters = b.TrainAuthorityMeters,
            MovementRequestId = !b.IsOccupied || b.CodeAppliesToOccupant ? b.MovementRequestId : string.Empty,
            Signal = b.EffectiveSignal, Beacon = b.Beacon, BeaconTargetBlockId = b.BeaconTargetBlockId, ElevationMeters = b.ElevationMeters,
            GradePercent = b.GradePercent, TemperatureCelsius = b.TemperatureCelsius, WaitingPassengers = b.WaitingPassengers,
            BoardingPassengers = b.BoardingPassengers, DisembarkingPassengers = b.DisembarkingPassengers, TicketsSold = b.TicketsSold,
            HasHeater = b.HasHeater, HeaterOn = b.HeaterOn, SpeedLimitMetersPerSecond = b.SpeedLimitMetersPerSecond,
            TravelDirection = b.TravelDirection,
            NextBlockId = b.NextBlockId };
    }

    public TicketSalesMessage CreateTicketSales(string lineId)
    {
        // Throughput is the number of tickets sold during the preceding simulation hour.
        var tickets = _sales.Where(s => s.LineId == lineId).Sum(s => (long)s.Count);
        return new() { LineId = lineId, TicketsPerHour = (int)Math.Min(int.MaxValue, tickets) };
    }

    private TrackBlock RequireBlock(string id) => FindBlock(id) ?? throw new ArgumentException($"Unknown block '{id}'.");
    private static void Nonnegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentException(name + " must be a finite, nonnegative number.");
    }
    private void Changed()
    {
        ForgetCompletedPassengerStops();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
