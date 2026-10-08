using System.Windows.Threading;
using TrainControl.Common.Communication;
namespace TrackModel.TestUI.Wpf.Services;

/// <summary>Owns the actual receiving endpoints of the external modules during standalone testing.
/// Never constructs or references TrackModel.Core.</summary>
public sealed class ExternalModuleSimulator(Dispatcher dispatcher) : IExternalModuleConnection
{
    public event Action<string, MessageEnvelope>? MessageReceived;
    public event Action<string>? ErrorReported;
    public Task SendAsync(object message) => NamedPipeTransport.SendAsync(NamedPipeNames.TrackModel, message);
    public Task RunAsync(CancellationToken token) => Task.WhenAll(
        Listen("Track Controller", NamedPipeNames.TrackController, token),
        Listen("Train Model", NamedPipeNames.TrainModel, token),
        Listen("TestUI setup / feedback", NamedPipeNames.TrackModelTestUi, token),
        Listen("CTC", NamedPipeNames.Ctc, token));

    private Task Listen(string name, string pipe, CancellationToken token) => NamedPipeTransport.ListenAsync(pipe,
        async envelope => await dispatcher.InvokeAsync(() => MessageReceived?.Invoke(name, envelope)),
        ex => dispatcher.InvokeAsync(() => ErrorReported?.Invoke(name + ": " + ex.Message)), token);
}
