using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Carries a batch of measured block feedback for one validation operation.</summary>
public sealed class WaysideFeedbackMessage
{
    public List<WaysideBlockFeedback> Blocks { get; set; } = [];
}
