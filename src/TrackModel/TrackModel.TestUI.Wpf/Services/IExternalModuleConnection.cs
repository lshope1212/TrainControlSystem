using TrainControl.Common.Communication;

namespace TrackModel.TestUI.Wpf.Services;

/// <summary>The tester exchanges contracts; it does not own track state.</summary>
public interface IExternalModuleConnection
{
    event Action<string, MessageEnvelope>? MessageReceived;
    event Action<string>? ErrorReported;
    Task SendAsync(object message);
}
