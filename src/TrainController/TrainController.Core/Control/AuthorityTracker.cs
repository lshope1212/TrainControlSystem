using TrainController.Abstractions.Inputs;

namespace TrainController.Core.Control;

/// <summary>
/// Remaining-authority estimate for ONE train.
/// </summary>
/// <remarks>
/// <para>
/// A received authority value is TRUSTED only while the track signal is valid
/// (<see cref="TrainModelInput.TrackSignalValid"/>). With a valid signal the estimate is
/// initialized from the first received value and recalibrated whenever a NEW authority update
/// arrives (<see cref="TrainModelInput.AuthorityUpdateReceived"/>).
/// </para>
/// <para>
/// With an invalid signal, received authority values are ignored (never recalibrated from);
/// the last trusted estimate keeps decreasing by ActualSpeed × DeltaTime, clamped at 0.
/// Restoring the signal does not by itself recalibrate: the next authority update received
/// while the signal is valid does.
/// </para>
/// <para>
/// If no trusted value exists yet and the signal is invalid, the remaining authority is 0
/// (no authority = no movement permitted) until the first valid-signal tick.
/// </para>
/// </remarks>
public sealed class AuthorityTracker
{
    public bool HasValue { get; private set; }

    public double RemainingMeters { get; private set; }

    /// <summary>
    /// Updates the estimate for one tick. Returns true when it was recalibrated from a received
    /// (trusted) value; <paramref name="updateIgnored"/> is true when a newly received value was
    /// discarded because the track signal was invalid.
    /// </summary>
    public bool Update(TrainModelInput model, double deltaTimeSeconds, out bool updateIgnored)
    {
        ArgumentNullException.ThrowIfNull(model);
        updateIgnored = false;

        if (model.TrackSignalValid && (!HasValue || model.AuthorityUpdateReceived))
        {
            HasValue = true;
            RemainingMeters = Math.Max(0.0, model.RemainingAuthorityMeters);
            return true;
        }

        updateIgnored = !model.TrackSignalValid && model.AuthorityUpdateReceived;

        if (!HasValue)
        {
            RemainingMeters = 0.0;
            return false;
        }

        var travelled = Math.Max(0.0, model.ActualSpeedMetersPerSecond) * deltaTimeSeconds;
        RemainingMeters = Math.Max(0.0, RemainingMeters - travelled);
        return false;
    }

    public void Reset()
    {
        HasValue = false;
        RemainingMeters = 0.0;
    }
}
