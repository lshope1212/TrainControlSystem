using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Hardware;
using TrainController.Abstractions.Validation;
using TrainController.Hardware.Pi.Controller;

namespace TrainController.Hardware.Pi;

/// <summary>
/// Turns ONE received message into ONE reply. A ControllerRequest executes exactly one control
/// step for exactly one Hardware train using that train's own controller state.
/// Thread-safe (requests are processed one at a time).
/// </summary>
public sealed class HardwareRequestHandler
{
    private readonly object _gate = new object();
    private readonly IReadOnlyDictionary<string, HardwareTrainController> _trains;

    public HardwareRequestHandler()
    {
        _trains = TrainFleet.HardwareTrainIds.ToDictionary(id => id, id => new HardwareTrainController(id), StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one representative request through serialization, this handler and a controller on a
    /// THROWAWAY handler, so one-time JIT work happens before the first real request. Returns the
    /// time it took.
    /// </summary>
    public static TimeSpan WarmUp()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        HardwareProtocol.WarmUp();

        var scratch = new HardwareRequestHandler();
        var request = HardwareProtocol.Deserialize(HardwareProtocol.Serialize(HardwareProtocol.SampleRequest()));
        HardwareProtocol.Serialize(scratch.Handle(request));
        HardwareProtocol.Serialize(scratch.Handle(new HardwareEnvelope { Type = HardwareMessageType.Hello }));

        return started.Elapsed;
    }

    /// <summary>The five Hardware trains served by this Pi.</summary>
    public IReadOnlyList<string> ServedTrainIds => TrainFleet.HardwareTrainIds;

    /// <summary>Per-train state, exposed for diagnostics and tests.</summary>
    public HardwareTrainController Controller(string trainId) =>
        _trains.TryGetValue(trainId, out var controller)
            ? controller
            : throw new ArgumentException($"'{trainId}' is not a Hardware train served by this Pi.", nameof(trainId));

    /// <summary>Never throws: failures become an <see cref="HardwareMessageType.ErrorResponse"/>.</summary>
    public HardwareEnvelope Handle(HardwareEnvelope request)
    {
        if (request is null)
        {
            return Error(null, "Null request.");
        }

        if (request.ProtocolVersion != HardwareProtocol.Version)
        {
            return Error(request, $"Unsupported protocol version {request.ProtocolVersion} (Pi speaks {HardwareProtocol.Version}).");
        }

        try
        {
            lock (_gate)
            {
                return request.Type switch
                {
                    HardwareMessageType.Hello => Reply(request, HardwareMessageType.HelloResponse) with { ServedTrainIds = ServedTrainIds.ToArray() },
                    HardwareMessageType.ResetRequest => HandleReset(request),
                    HardwareMessageType.ControllerRequest => HandleStep(request),
                    _ => Error(request, $"Unexpected message type {request.Type}."),
                };
            }
        }
        catch (Exception ex)
        {
            return Error(request, $"Pi processing error: {ex.Message}");
        }
    }

    private HardwareEnvelope HandleReset(HardwareEnvelope request)
    {
        if (string.IsNullOrEmpty(request.TrainId))
        {
            foreach (var controller in _trains.Values)
            {
                controller.Reset();
            }
        }
        else
        {
            Controller(request.TrainId).Reset();
        }

        return Reply(request, HardwareMessageType.ResetResponse);
    }

    private HardwareEnvelope HandleStep(HardwareEnvelope request)
    {
        var input = request.Input;
        if (input is null)
        {
            return Error(request, "ControllerRequest without input.");
        }

        if (input.TrainId != request.TrainId || input.TickId != request.TickId)
        {
            return Error(request, "Input identity does not match the request header.");
        }

        if (!_trains.TryGetValue(request.TrainId, out var controller))
        {
            return Error(request, $"'{request.TrainId}' is not a Hardware train served by this Pi.");
        }

        var output = controller.Step(input);

        var check = TrainControllerOutputValidator.Validate(output, request.TrainId, request.TickId, input.Vehicle);
        if (!check.IsValid)
        {
            return Error(request, $"Hardware controller produced an invalid output: {check}");
        }

        return Reply(request, HardwareMessageType.ControllerResponse) with { Output = output };
    }

    private static HardwareEnvelope Reply(HardwareEnvelope request, HardwareMessageType type) => new HardwareEnvelope
    {
        ProtocolVersion = HardwareProtocol.Version,
        Type = type,
        RequestId = request.RequestId,
        TrainId = request.TrainId,
        TickId = request.TickId,
    };

    private static HardwareEnvelope Error(HardwareEnvelope? request, string message) => new HardwareEnvelope
    {
        ProtocolVersion = HardwareProtocol.Version,
        Type = HardwareMessageType.ErrorResponse,
        RequestId = request?.RequestId ?? -1,
        TrainId = request?.TrainId ?? string.Empty,
        TickId = request?.TickId ?? -1,
        Error = message,
    };
}
