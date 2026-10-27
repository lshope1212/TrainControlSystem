namespace TrainModel.Wpf.ViewModels;

/// <summary>
/// A single labelled interface row with the wireframe's INPUT/OUTPUT badge, a
/// status dot, a source sub-label, and the (imperial, preformatted) value.
/// </summary>
public sealed class IoRowViewModel : ViewModelBase
{
    public IoRowViewModel(string label, string direction, string source)
    {
        Label = label;
        Direction = direction;   // "INPUT" or "OUTPUT"
        Source = source;         // e.g. "TRACK · IN", "CONTROLLER · IN", "TRAIN · OUT"
    }

    public string Label { get; }
    public string Direction { get; }
    public bool IsInput => Direction == "INPUT";
    public string Source { get; }

    private string _value = "--";
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}
