using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Fleet;

namespace TrainController.Integration.State;

/// <summary>
/// Owns one independent <see cref="TrainRuntimeSlot"/> for each of the ten fleet trains.
/// Exactly one registry exists per Train Controller process (created in the composition root).
/// </summary>
/// <remarks>
/// Which train a UI currently SHOWS is a UI concern and is not stored here; whether a train
/// RUNS comes from its model input (<c>IsActive</c>). The two are never coupled.
/// </remarks>
public sealed class TrainStateRegistry
{
    private readonly IReadOnlyDictionary<string, TrainRuntimeSlot> _slots;

    /// <param name="startupDefaults">
    /// PROVISIONAL initial Driver / Test Model values; <see cref="ControllerStartupDefaults.Default"/> when null.
    /// </param>
    public TrainStateRegistry(ControllerStartupDefaults? startupDefaults = null)
    {
        StartupDefaults = startupDefaults ?? ControllerStartupDefaults.Default;
        var trains = TrainFleet.AllTrainIds.Select(id => new TrainRuntimeSlot(id, StartupDefaults)).ToArray();
        Trains = trains;
        _slots = trains.ToDictionary(slot => slot.TrainId, StringComparer.Ordinal);
    }

    public ControllerStartupDefaults StartupDefaults { get; }

    /// <summary>All ten trains in TRAIN-001 .. TRAIN-010 order.</summary>
    public IReadOnlyList<TrainRuntimeSlot> Trains { get; }

    /// <exception cref="UnknownTrainException"><paramref name="trainId"/> is not a fleet train.</exception>
    public TrainRuntimeSlot Get(string? trainId)
    {
        if (trainId is not null && _slots.TryGetValue(trainId, out var slot))
        {
            return slot;
        }

        throw new UnknownTrainException(trainId);
    }

    /// <summary>Windows-side part of Test Simulation Reset, for all ten trains.</summary>
    public void ResetAllRuntime()
    {
        foreach (var slot in Trains)
        {
            slot.ResetRuntime();
        }
    }
}
