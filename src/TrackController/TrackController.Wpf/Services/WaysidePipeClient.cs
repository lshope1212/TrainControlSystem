using TrainControl.Common.Communication;

namespace TrackController.Wpf.Services;

/// <summary>No controller reference or in-process fallback. The same sender used by the other modules.</summary>
public sealed class WaysidePipeClient(string inputPipe)
{
    /// <summary>Sends an application message using the same serializer and named-pipe transport as live modules.</summary>
    public Task SendAsync(object message, CancellationToken cancellationToken = default) => NamedPipeTransport.SendAsync(inputPipe, message, cancellationToken);
}
