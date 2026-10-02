using CTC.TestUI.Wpf.Services;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Base for simulated-input view models: each builds a real shared contract message and
/// hands it to <see cref="SendToCtcAsync{TMessage}"/>. None may contain CTC behavior.
/// </summary>
public abstract class CtcInputViewModelBase : ViewModelBase
{
    private readonly ICtcMessageSender _sender;
    private readonly CommunicationStatusViewModel _communicationStatus;
    private string _result = string.Empty;

    protected CtcInputViewModelBase(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _communicationStatus = communicationStatus ?? throw new ArgumentNullException(nameof(communicationStatus));
    }

    /// <summary>Outcome of this input's most recent send.</summary>
    public string Result
    {
        get => _result;
        protected set => SetProperty(ref _result, value);
    }

    /// <summary>
    /// Sends <paramref name="message"/> and reports the outcome. Success only means CTC's
    /// endpoint received the bytes; there is no acknowledgement protocol.
    /// </summary>
    /// <param name="detail">Optional suffix for the success text, e.g. " for Block G12".</param>
    protected async Task SendToCtcAsync<TMessage>(TMessage message, string detail = "")
        where TMessage : class
    {
        var messageType = typeof(TMessage).Name;

        try
        {
            await _sender.SendToCtcAsync(message);
            Result = $"{messageType} sent to CTC{detail}.";
        }
        catch (Exception ex)
        {
            // Runs from an async void command: report, never crash the TestUI.
            Result = $"Unable to send {messageType}: {ex.Message}";
        }

        _communicationStatus.ReportCtcSend(Result);
    }
}
