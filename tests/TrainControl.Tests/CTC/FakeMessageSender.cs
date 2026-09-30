using CTC.Core.Interfaces;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Test double that records outgoing messages instead of transmitting them.
/// </summary>
internal sealed class FakeMessageSender : IMessageSender
{
    public List<object> SentMessages { get; } = new List<object>();

    /// <summary>When set, SendAsync throws this instead of recording the message.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        SentMessages.Add(message);
        return Task.CompletedTask;
    }
}
