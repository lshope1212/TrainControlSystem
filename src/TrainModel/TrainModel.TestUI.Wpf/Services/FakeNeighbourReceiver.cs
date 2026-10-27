using System.Windows.Threading;
using TrainControl.Common.Communication;

namespace TrainModel.TestUI.Wpf.Services;

/// <summary>
/// Simulates the Train Model's neighbours (Train Controller and Track Model) by
/// listening on their inbound pipes, so the harness can display what the Train
/// Model SENDS OUT. Do not run alongside the real neighbour processes — only one
/// process can own each pipe.
/// </summary>
public sealed class FakeNeighbourReceiver
{
    private readonly Dispatcher _dispatcher;
    private readonly string _pipeName;

    public FakeNeighbourReceiver(string pipeName, Dispatcher dispatcher)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public string PipeName => _pipeName;

    public event EventHandler<MessageEnvelope>? MessageReceived;
    public event EventHandler<string>? ErrorOccurred;

    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(_pipeName, OnMessageAsync, OnError, cancellationToken);

    private async Task OnMessageAsync(MessageEnvelope envelope) =>
        await _dispatcher.InvokeAsync(() => MessageReceived?.Invoke(this, envelope));

    private void OnError(Exception exception) =>
        _dispatcher.InvokeAsync(() => ErrorOccurred?.Invoke(this, exception.Message));
}
