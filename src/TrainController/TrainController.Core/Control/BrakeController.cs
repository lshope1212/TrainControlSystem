using TrainController.Abstractions.Outputs;

namespace TrainController.Core.Control;

/// <summary>Result of one emergency-brake evaluation.</summary>
/// <param name="ResetBlockedReason">Empty if no reset is needed or one would be accepted now; otherwise why not.</param>
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
/// Emergency-brake latch for ONE train.
/// </summary>
/// <remarks>
/// <para>
/// Every emergency source (driver press, passenger request, authority violation and — only
/// when configured — track signal loss) sets a
/// latched cause. The emergency brake stays applied while ANY cause is latched; a source
/// clearing on its own never releases the brake.
/// </para>
/// <para>
/// A driver reset clears the latch only when no emergency condition is still active this
/// tick (passenger request no longer present, no authority violation, no configured
/// track-signal-loss emergency). Otherwise the reset
/// is rejected and the latch stays. A new press on the same tick as a reset wins.
/// </para>
/// </remarks>
public sealed class BrakeController
{
    public EmergencyBrakeCause LatchedCauses { get; private set; }

    public EmergencyBrakeEvaluation Evaluate(
        bool driverEmergencyBrakePressed,
        bool passengerEmergencyBrakeRequested,
        bool authorityViolation,
        bool trackSignalLossEmergency,
        bool driverResetRequested)
    {
        var resetAccepted = false;
        var resetRejected = false;
        var rejectedReason = string.Empty;

        var blocker = passengerEmergencyBrakeRequested
            ? "passenger emergency brake request is still active"
            : authorityViolation
                ? "train still cannot stop within its remaining authority"
                : trackSignalLossEmergency
                    ? "track signal is still lost"
                    : string.Empty;

        if (driverResetRequested && LatchedCauses != EmergencyBrakeCause.None)
        {
            if (blocker.Length > 0)
            {
                resetRejected = true;
                rejectedReason = blocker;
            }
            else
            {
                LatchedCauses = EmergencyBrakeCause.None;
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

        if (trackSignalLossEmergency)
        {
            LatchedCauses |= EmergencyBrakeCause.TrackSignalLoss;
        }

        var blockedNow = LatchedCauses != EmergencyBrakeCause.None ? blocker : string.Empty;
        return new EmergencyBrakeEvaluation(LatchedCauses, resetAccepted, resetRejected, rejectedReason, blockedNow);
    }

    public void Reset() => LatchedCauses = EmergencyBrakeCause.None;
}
