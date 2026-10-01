using System.Collections.ObjectModel;
using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Models;

namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Test-harness stand-in for the production NamedPipeMessageSender. Instead of
/// transmitting anything, it records every message CTC.Core asks to send so the Test UI
/// can show exactly what would have gone to another module.
/// </summary>
/// <remarks>
/// <see cref="Messages"/> is modified on the calling thread. That is fine while CTCService
/// is only invoked from the WPF UI thread; if CTC.Core ever sends from background threads,
/// marshal the Add onto the dispatcher here.
/// </remarks>
public sealed class RecordingMessageSender : IMessageSender
{
    private int _nextSequenceNumber = 1;

    public ObservableCollection<RecordedMessage> Messages { get; } = new ObservableCollection<RecordedMessage>();

    public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        Messages.Add(new RecordedMessage(_nextSequenceNumber++, DateTime.Now, message));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears captured messages. Sequence numbers keep increasing so rows from before and
    /// after a clear are never confused.
    /// </summary>
    public void Clear() => Messages.Clear();
}
