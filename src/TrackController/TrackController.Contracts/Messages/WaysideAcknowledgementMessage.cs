using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;
/// <summary>Application result published on the diagnostic monitor; transport delivery alone is not acceptance.</summary>
public sealed record WaysideAcknowledgementMessage(
    [property: JsonPropertyName("accepted")] bool IsAccepted,
    string Detail,
    string MessageType = "");
