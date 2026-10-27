namespace TrainModel.TestUI.Wpf.Services;

/// <summary>Raised when a simulated message cannot be delivered to the Train Model.</summary>
public sealed class TrainModelSendException : Exception
{
    public TrainModelSendException(string message, Exception inner) : base(message, inner) { }
}
