using TrainControl.Common.Communication;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>Surfaces the harness's endpoint names and listener status.</summary>
public sealed class CommunicationStatusViewModel : ViewModelBase
{
    private string _trainModelEndpointStatus = "Not yet sent. The Train Model hosts the inbound pipe; this harness connects on send.";
    private string _neighbourListenerStatus = "Listening for messages the Train Model sends to its neighbours.";

    public string TrainModelPipeName => NamedPipeNames.TrainModel;

    public string TrainModelEndpointStatus
    {
        get => _trainModelEndpointStatus;
        set => SetProperty(ref _trainModelEndpointStatus, value);
    }

    public string NeighbourListenerStatus
    {
        get => _neighbourListenerStatus;
        set => SetProperty(ref _neighbourListenerStatus, value);
    }
}
