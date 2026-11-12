using TrainControl.Contracts.Messages;
using TrainModel.Core.Models;

namespace TrainModel.Core.Services;

/// <summary>
/// The train's track-circuit pickup: selects, from the Track Model's per-block
/// <see cref="TrackModelTrainEnvironmentMessage"/>s, the one describing the block a train occupies,
/// and applies its commanded speed and authority (plus grade) to that train's inputs.
/// </summary>
/// <remarks>
/// <para>
/// Ordering: a snapshot is applied only if its <see cref="TrackModelTrainEnvironmentMessage.Sequence"/>
/// is newer than the last one applied to that train, so a late or replayed message can never restore
/// authority that a newer snapshot withdrew. Unsequenced (legacy/test) messages are applied in arrival
/// order, as before.
/// </para>
/// <para>
/// Values are relayed exactly as received. A zero is an authoritative stop code and is applied
/// immediately, like any other value: there is no filtering, hold-last-value or grace period here.
/// The Track Model already gives a train entering a block with a prepared handoff its continuing
/// authority, so no intermediate zero arises from a request replacement.
/// </para>
/// </remarks>
public sealed class TrackCircuitReceiver
{
    private readonly Dictionary<string, long> _lastSequence = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies <paramref name="environment"/> to <paramref name="inputs"/> when it names
    /// <paramref name="trainId"/> and is not older than the latest applied. True when applied.
    /// </summary>
    public bool TryApply(string trainId, TrackModelTrainEnvironmentMessage environment, TrainInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrEmpty(trainId) || !string.Equals(environment.TrainId, trainId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!double.IsFinite(environment.CommandedSpeedMetersPerSecond) || environment.CommandedSpeedMetersPerSecond < 0
            || !double.IsFinite(environment.AuthorityMeters) || environment.AuthorityMeters < 0
            || !double.IsFinite(environment.GradePercent))
        {
            return false;
        }

        if (environment.Sequence > 0)
        {
            if (_lastSequence.TryGetValue(trainId, out var last) && environment.Sequence <= last)
            {
                return false;
            }

            _lastSequence[trainId] = environment.Sequence;
        }

        inputs.CommandedSpeedMetersPerSecond = environment.CommandedSpeedMetersPerSecond;
        inputs.CommandedAuthorityMeters = environment.AuthorityMeters;
        inputs.GradePercent = environment.GradePercent;
        // Passenger demand also rides the combined environment message; apply it so boarding
        // uses the Track Model's station population (Derrick point 1). A dedicated
        // StationPassengerMessage, if also sent, updates the same field — last writer wins.
        if (environment.WaitingPassengers >= 0)
        {
            inputs.PassengerDemand = environment.WaitingPassengers;
        }
        return true;
    }

    /// <summary>Forgets ordering for a train (e.g. it was removed from service).</summary>
    public void Forget(string trainId) => _lastSequence.Remove(trainId);
}
