using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>SI units throughout. Blocks describe physical topology; ownership is independent of its edges.</summary>
public sealed class WaysideConfigurationMessage
{
    public string WaysideId { get; set; } = "HW-TC01";

    public string LineName { get; set; } = "Green Line";

    public double FeedbackTimeoutSeconds { get; set; } = 5;

    public double RequestTimeoutSeconds { get; set; } = 10;

    public List<WaysideBlockDefinition> Blocks { get; set; } = [];

    /// <summary>Null preserves legacy ownership of every supplied block. Otherwise only these blocks may be commanded.</summary>
    public List<string>? OwnedBlockIds { get; set; }

    /// <summary>Private routed-runtime mode: evaluate shared routes; publish commands only for owned blocks.</summary>
    public bool CoordinatedRoutes { get; set; }

    /// <summary>Legacy external reservation service. Course CTC uses wayside-owned movement locks instead.</summary>
    public bool RequireCtcReservationFeedback { get; set; } = true;
}
