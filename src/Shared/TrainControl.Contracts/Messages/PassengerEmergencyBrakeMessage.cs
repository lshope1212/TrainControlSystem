namespace TrainControl.Contracts.Messages;

/// <summary>
/// Passenger-initiated emergency brake request / status. A passenger may REQUEST
/// the brake but may not reset it: clearing the emergency brake is settled by the
/// Train Controller, never by the passenger. This message therefore only ever
/// carries a request (true) or the controller-cleared status (false).
/// </summary>
public class PassengerEmergencyBrakeMessage
{
    public string TrainId { get; set; } = string.Empty;

    public bool Requested { get; set; }
}
