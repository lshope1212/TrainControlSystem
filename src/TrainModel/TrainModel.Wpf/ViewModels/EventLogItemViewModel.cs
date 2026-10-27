namespace TrainModel.Wpf.ViewModels;

/// <summary>One entry in the Event & Core I/O Log.</summary>
public sealed class EventLogItemViewModel
{
    public string Timestamp { get; init; } = DateTime.Now.ToString("HH:mm:ss");
    public string Severity { get; init; } = "INFO";   // INFO / WARN / ALERT
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
