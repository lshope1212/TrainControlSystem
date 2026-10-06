using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>The same application messages are handled locally or on the assigned Pi.</summary>
public interface IWaysideRuntime : IAsyncDisposable
{
    WaysideSnapshotMessage LatestSnapshot
    {
        get;
    }

    /// <summary>Handles an application message through the selected runtime and returns its result.</summary>
    Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default);
    /// <summary>Refreshes runtime outputs so timeouts apply even when no new input arrives.</summary>
    Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates envelopes and rejection receipts using the existing team message format.</summary>
public static class RuntimeMessages
{
    /// <summary>Uses the common serializer to construct the same envelope accepted by module receivers.</summary>
    public static MessageEnvelope CreateEnvelope(object value) => MessageSerializer.Deserialize(MessageSerializer.Serialize(value));
    /// <summary>Creates an application rejection receipt without implying transport failure.</summary>
    public static WaysideAcknowledgementMessage CreateRejection(string type, string detail) => new(false, detail, type);
}
