using System.IO;
using CTC.Core.Exceptions;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Services;

/// <summary>
/// Sends clock-control requests to the shared simulation clock, which the Launcher hosts on
/// <see cref="NamedPipeNames.SimulationClock"/>.
/// </summary>
public sealed class NamedPipeSimulationClockControlSender : ISimulationClockControlSender
{
    private const string NotRunningReason = "The simulation clock is not running (start the system from the Launcher).";

    public async Task SendAsync(SimulationTimeControlMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await NamedPipeTransport.SendAsync(NamedPipeNames.SimulationClock, request, cancellationToken, connectTimeoutMilliseconds: 1000);
        }
        catch (TimeoutException ex)
        {
            throw new MessageSendException(NotRunningReason, ex);
        }
        catch (IOException ex)
        {
            throw new MessageSendException("The connection to the simulation clock failed while sending.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageSendException("Access to the simulation clock pipe was denied.", ex);
        }
    }
}
