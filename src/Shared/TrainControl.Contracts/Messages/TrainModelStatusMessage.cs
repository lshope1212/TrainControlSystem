using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// State of one train as seen by its on-board Train Controller.
/// Direction: Train Model -> Train Controller.
/// </summary>
/// <remarks>
/// <para>
/// PROPOSED contract: the Train Model interface does not define these fields yet. All values
/// are SI. The Train Model does not know (and must not know) whether the train is driven by
/// the Software or the Hardware Train Controller.
/// </para>
/// <para>
/// <see cref="AuthorizedSpeedMetersPerSecond"/> and <see cref="RemainingAuthorityMeters"/>
/// are the values the wayside has authorized, relayed to the train through the track.
/// The Train Controller never reads the track-layout workbook itself.
/// </para>
/// <para>
/// Beacon fields describe the most recently received beacon. How the Train Model signals that
/// a beacon has JUST been received (a separate event, a one-tick flag, ...) is not yet defined
/// by the Train Model interface, so this contract deliberately imposes no event identifier.
/// Detecting a new reception is the Train Controller input adapter's job once that mechanism
/// is agreed; every newly received valid beacon must recalibrate station distance.
/// </para>
/// </remarks>
public class TrainModelStatusMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>
    /// True while the overall system has dispatched the train (CTC -> Track Controller ->
    /// Train Model) and it is in service. Owned by the Train Model / system, never by the
    /// Train Controller, which only executes controller ticks for active trains.
    /// </summary>
    public bool IsActive { get; set; }

    public double ActualSpeedMetersPerSecond { get; set; }

    public double AuthorizedSpeedMetersPerSecond { get; set; }

    public double RemainingAuthorityMeters { get; set; }

    /// <summary>False when the track-circuit speed/authority signal is lost or invalid.</summary>
    public bool TrackSignalValid { get; set; }

    /// <summary>True when the beacon fields below carry valid data.</summary>
    public bool BeaconValid { get; set; }

    public string NextStationName { get; set; } = string.Empty;

    /// <summary>Distance from the beacon to the next station, at the moment the beacon was read.</summary>
    public double DistanceToNextStationMeters { get; set; }

    public PlatformSide PlatformSide { get; set; } = PlatformSide.None;

    public bool PassengerEmergencyBrakeRequested { get; set; }

    public bool LeftDoorsOpen { get; set; }

    public bool RightDoorsOpen { get; set; }

    public bool ExteriorLightsOn { get; set; }

    public double CabinTemperatureCelsius { get; set; }
}
