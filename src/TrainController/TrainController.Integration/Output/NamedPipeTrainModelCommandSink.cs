using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrainController.Integration.Output;

/// <summary>
/// Normal mode: sends each train's <see cref="TrainControllerCommandMessage"/> (tagged with the
/// TickId of the Train Model state it answers) to the Train Model's pipe
/// (<see cref="NamedPipeNames.TrainModel"/>). A Train Model that is not running surfaces as an
/// exception, which <see cref="OutputRouter"/> logs.
/// </summary>
public sealed class NamedPipeTrainModelCommandSink : ITrainModelCommandSink
{
    /// <summary>Short connect timeout: a missing Train Model must not stall the tick for seconds.</summary>
    public const int ConnectTimeoutMilliseconds = 250;

    public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken) =>
        NamedPipeTransport.SendAsync(NamedPipeNames.TrainModel, message, cancellationToken, ConnectTimeoutMilliseconds);
}
