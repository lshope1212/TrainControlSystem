using System.Windows.Threading;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Simulates the Track Controller's receiving endpoint.
///
/// The real CTC sends Track Controller messages through the
/// TrackController named pipe. This class listens to that pipe
/// so the TestUI can display those outgoing messages.
///
/// Do not run this at the same time as the real Track Controller,
/// because only one process can own the pipe. 
/// </summary>
public sealed class FakeTrackControllerReceiver
{
    private readonly Dispatcher _dispatcher;

    public FakeTrackControllerReceiver(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string PipeName => NamedPipeNames.TrackController;

    // Events
    /// <summary>Raised when the TestUI receives a valid message sent by the CTC.</summary>
    public event EventHandler<MessageEnvelope>? MessageReceived;

    /// <summary>Raised when an error occurs while listening to the pipe.</summary>
    public event EventHandler<string>? ErrorOccurred;

    // Public methods
    /// <summary>Starts listening for messages intended for the Track Controller.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return NamedPipeTransport.ListenAsync(PipeName, OnMessageAsync, OnError, cancellationToken);
    }

    // Message handling
    private async Task OnMessageAsync(MessageEnvelope envelope)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            MessageReceived?.Invoke(this, envelope);
        });
    }

    private void OnError(Exception exception)
    {
        _dispatcher.InvokeAsync(() =>
        {
            ErrorOccurred?.Invoke(this, exception.Message);
        });
    }
}
