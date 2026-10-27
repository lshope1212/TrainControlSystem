using TrainModel.TestUI.Wpf.Commands;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Shared plumbing for an input panel that builds one contract message and sends it
/// to the Train Model, surfacing a one-line Result. The target train id is NOT owned
/// by each panel — it comes from a single shared selector on the main view model via
/// <see cref="TrainIdProvider"/>, so one selection drives every panel.
/// </summary>
public abstract class TrainModelInputViewModelBase : ViewModelBase
{
    private string _result = string.Empty;

    protected TrainModelInputViewModelBase(ITrainModelMessageSender sender, Func<string> trainIdProvider)
    {
        Sender = sender ?? throw new ArgumentNullException(nameof(sender));
        TrainIdProvider = trainIdProvider ?? throw new ArgumentNullException(nameof(trainIdProvider));
        SendCommand = new AsyncRelayCommand(SendAsync);
    }

    protected ITrainModelMessageSender Sender { get; }

    /// <summary>Returns the currently-selected target train id (shared selector).</summary>
    protected Func<string> TrainIdProvider { get; }

    /// <summary>Convenience: the current target train id.</summary>
    /// <summary>The current target train id, normalized (trimmed + upper-cased) so it
    /// matches regardless of how it was typed or selected in the editable selector.</summary>
    protected string TargetTrainId => NormalizeTrainId(TrainIdProvider());

    /// <summary>Canonical train-id form used everywhere messages are routed by id.</summary>
    public static string NormalizeTrainId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().ToUpperInvariant();

    public AsyncRelayCommand SendCommand { get; }

    public string Result
    {
        get => _result;
        protected set => SetProperty(ref _result, value);
    }

    /// <summary>Build the message for this panel and send it.</summary>
    protected abstract Task SendAsync();

    /// <summary>Send one message and report success/failure into <see cref="Result"/>.</summary>
    protected async Task SendMessageAsync<TMessage>(TMessage message, string okText)
        where TMessage : class
    {
        try
        {
            await Sender.SendToTrainModelAsync(message);
            Result = $"{okText} @ {DateTime.Now:HH:mm:ss}";
        }
        catch (TrainModelSendException ex)
        {
            Result = $"Send failed: {ex.Message}";
        }
    }
}
