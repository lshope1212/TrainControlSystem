using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Integration.Mapping;

namespace TrainController.Integration.ModelInput;

/// <summary>
/// Normal Mode: holds the latest Train Model status per train, as delivered by a Train Model
/// adapter (future: named-pipe receiver). A train with no status yet yields no input and so
/// does not run.
/// </summary>
/// <remarks>
/// Integration point: the adapter calls <see cref="Submit"/>. Because the Train Model interface
/// has not defined how a beacon reception or an authority update is signalled, the adapter
/// passes <c>beaconNewlyReceived</c> / <c>authorityUpdated</c> explicitly. Each event is held
/// until the next tick consumes it, so one submitted between ticks is never lost.
/// </remarks>
public sealed class NormalModelInputProvider : IModelInputProvider
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, TrainModelStatusMessage> _latest = new Dictionary<string, TrainModelStatusMessage>(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingBeacon = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingAuthority = new HashSet<string>(StringComparer.Ordinal);

    /// <exception cref="UnknownTrainException">The message is for a train outside the fleet.</exception>
    public void Submit(TrainModelStatusMessage message, bool beaconNewlyReceived, bool authorityUpdated)
    {
        ArgumentNullException.ThrowIfNull(message);
        TrainFleet.EnsureKnown(message.TrainId);

        lock (_gate)
        {
            _latest[message.TrainId] = message;
            if (beaconNewlyReceived)
            {
                _pendingBeacon.Add(message.TrainId);
            }

            if (authorityUpdated)
            {
                _pendingAuthority.Add(message.TrainId);
            }
        }
    }

    public TrainModelInput? Peek(string trainId)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(trainId, out var message)
                ? TrainModelContractMapper.ToModelInput(message, _pendingBeacon.Contains(trainId), _pendingAuthority.Contains(trainId))
                : null;
        }
    }

    public TrainModelInput? TakeForTick(string trainId)
    {
        lock (_gate)
        {
            if (!_latest.TryGetValue(trainId, out var message))
            {
                return null;
            }

            var newlyReceived = _pendingBeacon.Remove(trainId);
            var authorityUpdated = _pendingAuthority.Remove(trainId);
            return TrainModelContractMapper.ToModelInput(message, newlyReceived, authorityUpdated);
        }
    }
}
