using System.Text.Json;
using System.Text.Json.Serialization;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;

namespace TrainController.Abstractions.Hardware;

/// <summary>Kinds of message exchanged between the Windows Hardware backend and the Raspberry Pi.</summary>
public enum HardwareMessageType
{
    /// <summary>Windows -> Pi, once per connection: version check / handshake.</summary>
    Hello = 0,

    /// <summary>Pi -> Windows: handshake reply listing the trains the Pi serves.</summary>
    HelloResponse,

    /// <summary>Windows -> Pi: execute exactly ONE controller step for one train and tick.</summary>
    ControllerRequest,

    /// <summary>Pi -> Windows: the output of that step.</summary>
    ControllerResponse,

    /// <summary>Windows -> Pi: reset runtime state (one train, or all when TrainId is empty).</summary>
    ResetRequest,

    /// <summary>Pi -> Windows: reset done.</summary>
    ResetResponse,

    /// <summary>Pi -> Windows: the request could not be processed (Windows fails safe).</summary>
    ErrorResponse
}

/// <summary>
/// One protocol message. JSON, one per length-prefixed frame. All physical values inside
/// <see cref="Input"/> / <see cref="Output"/> are SI; nothing imperial crosses this link.
/// </summary>
/// <remarks>
/// Every response echoes <see cref="ProtocolVersion"/>, <see cref="RequestId"/>,
/// <see cref="TrainId"/> and <see cref="TickId"/>; Windows rejects any response whose echo does
/// not match the outstanding request (wrong train, wrong tick, stale or duplicate response).
/// </remarks>
public sealed record HardwareEnvelope
{
    public int ProtocolVersion { get; init; } = HardwareProtocol.Version;

    public HardwareMessageType Type { get; init; }

    /// <summary>Connection-local, strictly increasing id assigned by Windows; echoed by the Pi.</summary>
    public long RequestId { get; init; }

    public string TrainId { get; init; } = string.Empty;

    public long TickId { get; init; }

    /// <summary>ControllerRequest only: the complete SI controller input (identity, timing, model, driver, engineer, vehicle, policy).</summary>
    public TrainControllerInput? Input { get; init; }

    /// <summary>ControllerResponse only.</summary>
    public TrainControllerOutput? Output { get; init; }

    /// <summary>HelloResponse only.</summary>
    public IReadOnlyList<string>? ServedTrainIds { get; init; }

    /// <summary>ErrorResponse only.</summary>
    public string? Error { get; init; }
}

/// <summary>Malformed or semantically invalid protocol message.</summary>
public sealed class HardwareProtocolException : Exception
{
    public HardwareProtocolException(string message)
        : base(message)
    {
    }

    public HardwareProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Version, JSON settings and (de)serialization of <see cref="HardwareEnvelope"/>.</summary>
public static class HardwareProtocol
{
    /// <summary>Bump whenever the message shape or meaning changes; both ends must agree exactly.</summary>
    public const int Version = 1;

    /// <summary>
    /// camelCase JSON, enums as strings (readable while debugging). Default number handling is
    /// kept on purpose: NaN / Infinity can neither be written nor read, so invalid numbers fail
    /// loudly instead of reaching a controller or the Train Model.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static byte[] Serialize(HardwareEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException)
        {
            throw new HardwareProtocolException($"Message could not be serialized: {ex.Message}", ex);
        }
    }

    /// <exception cref="HardwareProtocolException">Not valid JSON for a <see cref="HardwareEnvelope"/>.</exception>
    public static HardwareEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<HardwareEnvelope>(payload, JsonOptions)
                ?? throw new HardwareProtocolException("Message was JSON null.");
        }
        catch (JsonException ex)
        {
            throw new HardwareProtocolException($"Malformed message: {ex.Message}", ex);
        }
    }

    /// <summary>Representative request (all sections populated) used for warm-up.</summary>
    public static HardwareEnvelope SampleRequest(string trainId = "TRAIN-002") => new HardwareEnvelope
    {
        Type = HardwareMessageType.ControllerRequest,
        RequestId = 1,
        TrainId = trainId,
        TickId = 1,
        Input = new TrainControllerInput
        {
            TrainId = trainId,
            TickId = 1,
            SimulationTimeSeconds = 0.1,
            DeltaTimeSeconds = 0.1,
            Model = new TrainModelInput
            {
                IsActive = true,
                TrackSignalValid = true,
                ActualSpeedMetersPerSecond = 1.0,
                AuthorizedSpeedMetersPerSecond = 5.0,
                RemainingAuthorityMeters = 100.0,
                Beacon = new BeaconData { IsValid = true, IsNewlyReceived = true, NextStationName = "WARMUP", DistanceToStationMeters = 50.0 },
            },
        },
    };

    /// <summary>
    /// Exercises serialization of every message shape once, so the first REAL request after
    /// start-up is not slowed by one-time JIT / JSON metadata work (which can exceed the request
    /// timeout, especially on the Raspberry Pi). Never throws.
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            var request = Deserialize(Serialize(SampleRequest()));
            var reply = new HardwareEnvelope
            {
                Type = HardwareMessageType.ControllerResponse,
                RequestId = request.RequestId,
                TrainId = request.TrainId,
                TickId = request.TickId,
                Output = new TrainControllerOutput
                {
                    TrainId = request.TrainId,
                    TickId = request.TickId,
                    Display = new DriverDisplayState { Alerts = new[] { "warm-up" }, DistanceToNextStationMeters = 1.0 },
                },
                ServedTrainIds = new[] { request.TrainId },
            };
            Deserialize(Serialize(reply));
        }
        catch (Exception)
        {
            // Warm-up is an optimization only.
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
