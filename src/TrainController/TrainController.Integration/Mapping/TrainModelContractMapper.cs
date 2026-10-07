using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;

namespace TrainController.Integration.Mapping;

/// <summary>
/// Translates between the cross-subsystem Train Model contracts and the Train Controller's
/// internal records. Field-for-field, SI in and SI out; no behavior.
/// </summary>
public static class TrainModelContractMapper
{
    /// <param name="message">Latest Train Model status for one train.</param>
    /// <param name="beaconNewlyReceived">
    /// Whether a beacon reception arrived for this tick. The Train Model interface does not yet
    /// define how a reception is signalled, so the Normal-Mode input adapter (not this contract)
    /// supplies it once that mechanism is agreed.
    /// </param>
    /// <param name="authorityUpdated">
    /// Whether this status carries a NEW authority value. Like beacon receptions, the Train Model
    /// interface has not defined how updates are signalled, so the adapter supplies it.
    /// </param>
    public static TrainModelInput ToModelInput(TrainModelStatusMessage message, bool beaconNewlyReceived, bool authorityUpdated)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new TrainModelInput
        {
            IsActive = message.IsActive,
            ActualSpeedMetersPerSecond = message.ActualSpeedMetersPerSecond,
            AuthorizedSpeedMetersPerSecond = message.AuthorizedSpeedMetersPerSecond,
            RemainingAuthorityMeters = message.RemainingAuthorityMeters,
            AuthorityUpdateReceived = authorityUpdated,
            TrackSignalValid = message.TrackSignalValid,
            Beacon = new BeaconData
            {
                IsValid = message.BeaconValid,
                IsNewlyReceived = beaconNewlyReceived,
                NextStationName = message.NextStationName ?? string.Empty,
                DistanceToStationMeters = message.DistanceToNextStationMeters,
                PlatformSide = message.PlatformSide,
            },
            PassengerEmergencyBrakeRequested = message.PassengerEmergencyBrakeRequested,
            LeftDoorsOpen = message.LeftDoorsOpen,
            RightDoorsOpen = message.RightDoorsOpen,
            ExteriorLightsOn = message.ExteriorLightsOn,
            CabinTemperatureCelsius = message.CabinTemperatureCelsius,
        };
    }

    public static TrainControllerCommandMessage ToCommandMessage(TrainControllerOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var commands = output.Commands;

        return new TrainControllerCommandMessage
        {
            TrainId = output.TrainId,
            TickId = output.TickId,
            PowerCommandWatts = commands.PowerCommandWatts,
            ServiceBrakeCommand = commands.ServiceBrakeCommand,
            EmergencyBrakeCommand = commands.EmergencyBrakeCommand,
            LeftDoorsOpenCommand = commands.LeftDoorsOpenCommand,
            RightDoorsOpenCommand = commands.RightDoorsOpenCommand,
            ExteriorLightsCommand = commands.ExteriorLightsCommand,
            CabinTemperatureSetpointCelsius = commands.CabinTemperatureSetpointCelsius,
            StationAnnouncement = commands.StationAnnouncement,
        };
    }
}
