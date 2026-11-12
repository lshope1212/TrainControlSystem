using TrackModel.Core.Models;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Messages;

namespace TrackModel.Core.Services;

public partial class TrackService
{
    // The existing Train Model message has no exchange ID or station context. Conservatively
    // accept one exchange per stationary visit, not arbitrary repeated mutations of the platform.
    // A future identified exchange contract is needed for multiple exchanges at one stop and for
    // recognizing reports delayed until a different stop. This is not an exactly-once protocol.
    private readonly Dictionary<string, (TrackBlock Block, int Boarded, int Alighted, int LeftBehind)> _passengerStops = [];

    /// <summary>New customers entering a station: one ticket per arrival, independent of boarding.
    /// Initial layout populations and replacement demand snapshots are not new arrival events.</summary>
    public void RecordStationArrivals(string blockId, int count)
    {
        var station = RequireBlock(blockId);
        if (string.IsNullOrWhiteSpace(station.StationName))
            throw new ArgumentException("Customers must arrive at a station block.");
        if (count <= 0) throw new ArgumentException("Station arrivals must be a positive whole number.");
        if (count > int.MaxValue - station.WaitingPassengers || count > int.MaxValue - station.TicketsSold)
            throw new ArgumentException("Station passenger or ticket totals are too large.");

        station.WaitingPassengers += count;
        station.TicketsSold += count;
        _sales.Enqueue((SystemTime, station.LineId, count));
        Changed();
    }

    /// <summary>Applies the unchanged Train Model's exchange without moving its train or selling
    /// tickets. False means an identical report was already recorded at this stationary visit.</summary>
    public bool ApplyPassengerExchange(PassengerExchangeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.TrainId)) throw new ArgumentException("Train ID is required.");
        if (message.Boarded < 0 || message.Alighted < 0 || message.LeftBehind < 0)
            throw new ArgumentException("Passenger counts cannot be negative.");

        var trainId = PassengerTrainId(message.TrainId);
        var heads = Layout.Blocks.Where(b => b.IsOccupied && b.IsTrainHead && PassengerTrainId(b.TrainId) == trainId).ToList();
        if (heads.Count != 1) throw new InvalidOperationException("Passenger exchange needs exactly one known train head.");
        var station = heads[0];
        if (string.IsNullOrWhiteSpace(station.StationName)) throw new ArgumentException("Passenger exchange requires a station block.");
        if (station.ActualSpeedMetersPerSecond != 0) throw new ArgumentException("Passenger exchange requires a stopped train.");
        if (_trains.TryGetValue(trainId, out var tracked) && tracked.Status != TrainTrackingStatus.Active)
            throw new InvalidOperationException("Passenger exchange requires an active tracked train.");

        if (_passengerStops.TryGetValue(trainId, out var previous))
        {
            if (previous.Boarded == message.Boarded && previous.Alighted == message.Alighted && previous.LeftBehind == message.LeftBehind)
                return false;
            throw new InvalidOperationException("An unlabelled exchange was already recorded at this stop; another needs an exchange ID.");
        }
        if (message.Boarded > station.WaitingPassengers)
            throw new ArgumentException("Boarding exceeds the passengers waiting at this station.");
        if (message.Boarded > int.MaxValue - station.BoardingPassengers || message.Alighted > int.MaxValue - station.DisembarkingPassengers)
            throw new ArgumentException("Station passenger totals are too large.");
        // LeftBehind describes the Train Model's offer at exchange time. Do not replace the
        // current waiting population with it: other customers may have arrived meanwhile.
        if (message.Boarded == 0 && message.Alighted == 0) return false;
        station.WaitingPassengers -= message.Boarded;
        station.BoardingPassengers += message.Boarded;
        station.DisembarkingPassengers += message.Alighted;
        _passengerStops[trainId] = (station, message.Boarded, message.Alighted, message.LeftBehind);
        Changed();
        return true;
    }

    /// <summary>Only send a typed beacon when its stopping distance is explicitly configured.
    /// Existing beacon text alone does not establish a stopping point or a platform side.</summary>
    public BeaconMessage? CreateTrainBeacon(string blockId)
    {
        var block = RequireBlock(blockId);
        if (!block.IsOccupied || !block.IsTrainHead || !block.HasBeacon || block.BeaconDistanceToStopMeters is not { } distance)
            return null;
        var station = FindBlock(block.BeaconTargetBlockId);
        return new()
        {
            TrainId = block.TrainId,
            StationName = station?.StationName ?? block.Beacon,
            DistanceToStopMeters = distance,
            Side = block.BeaconPlatformSide,
        };
    }

    private static string PassengerTrainId(string id) => TrainIdConvention.TryNormalize(id, out var canonical) ? canonical : id;

    private void ForgetCompletedPassengerStops()
    {
        foreach (var (trainId, stop) in _passengerStops.ToArray())
            if (!Layout.Blocks.Contains(stop.Block) || !stop.Block.IsOccupied || !stop.Block.IsTrainHead
                || PassengerTrainId(stop.Block.TrainId) != trainId || stop.Block.ActualSpeedMetersPerSecond != 0)
                _passengerStops.Remove(trainId);
    }
}
