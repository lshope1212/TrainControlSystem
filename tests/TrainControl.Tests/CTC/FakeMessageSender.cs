using CTC.Core.Interfaces;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Test double that records outgoing messages instead of transmitting them.
/// </summary>
/// <remarks>
/// Physical dispatch traffic (<see cref="TrainDispatchRequestMessage"/>, <see cref="TrainRouteUpdateMessage"/>)
/// is recorded in <see cref="DispatchMessages"/>, apart from <see cref="SentMessages"/> (movement,
/// maintenance and switch requests), so tests about those requests keep counting only them. Every
/// message, dispatch included, is in <see cref="AllMessages"/> in send order.
/// </remarks>
internal sealed class FakeMessageSender : IMessageSender
{
    /// <summary>Movement, maintenance and switch requests, in send order.</summary>
    public List<object> SentMessages { get; } = new List<object>();

    /// <summary>Physical dispatch requests and route updates, in send order.</summary>
    public List<object> DispatchMessages { get; } = new List<object>();

    public List<object> AllMessages { get; } = new List<object>();

    /// <summary>When set, SendAsync throws this instead of recording the message.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        AllMessages.Add(message);
        (message is TrainDispatchRequestMessage or TrainRouteUpdateMessage ? DispatchMessages : SentMessages).Add(message);
        return Task.CompletedTask;
    }
}
