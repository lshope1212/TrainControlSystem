using System.Windows.Threading;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Temporarily plays the Track Controller's inbound endpoint: hosts the
/// <see cref="NamedPipeNames.TrackController"/> pipe server so messages the REAL running
/// CTC sends (via its production NamedPipeMessageSender) are received here and can be shown.
/// </summary>
/// <remarks>
/// Only one process can own this pipe, so the real Track Controller and this TestUI must
/// not run at the same time. Events are raised on the WPF UI thread.
/// </remarks>
public sealed class FakeTrackControllerReceiver
{
    private readonly Dispatcher _dispatcher;

    public FakeTrackControllerReceiver(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string PipeName => NamedPipeNames.TrackController;

    /// <summary>A well-formed envelope arrived from CTC.</summary>
    public event EventHandler<MessageEnvelope>? MessageReceived;

    /// <summary>Malformed data or a broken connection; listening continues.</summary>
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Listens until <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(PipeName, OnMessageAsync, OnError, cancellationToken);

    private async Task OnMessageAsync(MessageEnvelope envelope) =>
        await _dispatcher.InvokeAsync(() => MessageReceived?.Invoke(this, envelope));

    private void OnError(Exception ex) =>
        _dispatcher.InvokeAsync(() => ErrorOccurred?.Invoke(this, ex.Message));
}
