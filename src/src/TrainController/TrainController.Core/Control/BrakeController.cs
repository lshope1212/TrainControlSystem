using TrainController.Abstractions.Outputs;

namespace TrainController.Core.Control;

/// <summary>Result of one emergency-brake evaluation.</summary>
/// <param name="ResetBlockedReason">Empty if no E-brake reset is needed or one would be accepted now; otherwise why not.</param>
public readonly record struct EmergencyBrakeEvaluation(
    EmergencyBrakeCause LatchedCauses,
    bool ResetAccepted,
    bool ResetRejected,
    string ResetRejectedReason,
    string ResetBlockedReason)
{
    public bool EmergencyBrakeApplied => LatchedCauses != EmergencyBrakeCause.None;
}

/// <summary>
/// Emergency-brake latch for ONE train (driver, passenger and authority causes; brake-failure and
/// Hardware-link emergency brakes are owned by the failure handling, not by this latch).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmergencyBrakeCause.Driver"/> / <see cref="EmergencyBrakeCause.Passenger"/>: latched;
/// released only by the driver's E-brake reset, and only when the train is stopped and no passenger
/// request is still active. Otherwise the reset is rejected. A new press on the same tick as a
/// reset wins.
/// </para>
/// <para>
/// <see cref="EmergencyBrakeCause.AuthorityViolation"/>: a protection action, not a failure. It stays
/// applied while the train cannot stop within its authority and is released automatically on the
/// first tick the train is stopped (the authority limit and holding brake then keep it stopped).
/// The E-brake reset does not affect it.
/// </para>
/// </remarks>
public sealed class BrakeController
{
    private const EmergencyBrakeCause ManualCauses = EmergencyBrakeCause.Driver | EmergencyBrakeCause.Passenger;

    public EmergencyBrakeCause LatchedCauses { get; private set; }

    public EmergencyBrakeEvaluation Evaluate(
        bool driverEmergencyBrakePressed,
        bool passengerEmergencyBrakeRequested,
        bool authorityViolation,
        bool stopped,
        bool driverResetRequested)
    {
        var resetAccepted = false;
        var resetRejected = false;
        var rejectedReason = string.Empty;

        var blocker = passengerEmergencyBrakeRequested
            ? "passenger emergency brake request is still active"
            : !stopped
                ? "train is still moving"
                : string.Empty;

        if (driverResetRequested && (LatchedCauses & ManualCauses) != 0)
        {
            if (blocker.Length > 0)
            {
                resetRejected = true;
                rejectedReason = blocker;
            }
            else
            {
                LatchedCauses &= ~ManualCauses;
                resetAccepted = true;
            }
        }

        if (driverEmergencyBrakePressed)
        {
            LatchedCauses |= EmergencyBrakeCause.Driver;
        }

        if (passengerEmergencyBrakeRequested)
        {
            LatchedCauses |= EmergencyBrakeCause.Passenger;
        }

        if (authorityViolation)
        {
            LatchedCauses |= EmergencyBrakeCause.AuthorityViolation;
        }
        else if (stopped)
        {
            LatchedCauses &= ~EmergencyBrakeCause.AuthorityViolation;
        }

        var blockedNow = (LatchedCauses & ManualCauses) != 0 ? blocker : string.Empty;
        return new EmergencyBrakeEvaluation(LatchedCauses, resetAccepted, resetRejected, rejectedReason, blockedNow);
    }

    public void Reset() => LatchedCauses = EmergencyBrakeCause.None;
}
