using System.Diagnostics;
using System.Windows.Threading;
using CTC.Core.Interfaces;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Services;

/// <summary>
/// CTC's inbound endpoint. Hosts the <see cref="NamedPipeNames.Ctc"/> pipe server and routes
/// each received shared-contract message into the application's single
/// <see cref="ICTCService"/>, on the WPF UI thread.
/// </summary>
/// <remarks>
/// Its only job is JSON envelope -> contract -> matching ICTCService method; all CTC
/// behavior stays in CTC.Core. It does not know or care whether the sender is a real
/// module or CTC.TestUI.Wpf.
/// </remarks>
public sealed class CtcNamedPipeReceiver
{
    private readonly ICTCService _ctc;
    private readonly Dispatcher _dispatcher;

    public CtcNamedPipeReceiver(ICTCService ctc, Dispatcher dispatcher)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Readable outcome of each received message (or receive error). Raised on the UI thread.
    /// </summary>
    public event EventHandler<string>? StatusReported;

    /// <summary>Receives until <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(
            NamedPipeNames.Ctc,
            envelope => HandleAsync(envelope, cancellationToken),
            ReportError,
            cancellationToken);

    private async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        // Deserialize on the background thread; only the service call runs on the UI thread.
        Func<ICTCService, Task>? apply = envelope.MessageType switch
        {
            nameof(BlockStatusMessage) => Route<BlockStatusMessage>(envelope, (ctc, m) => ctc.ApplyBlockStatus(m)),
            nameof(TrackLayoutMessage) => Route<TrackLayoutMessage>(envelope, (ctc, m) => ctc.ApplyTrackLayout(m)),
            nameof(TrainAuthorizationStatusMessage) => Route<TrainAuthorizationStatusMessage>(envelope, (ctc, m) => ctc.ApplyTrainAuthorization(m)),
            nameof(TicketSalesMessage) => Route<TicketSalesMessage>(envelope, (ctc, m) => ctc.ApplyTicketSales(m)),
            nameof(SystemTimeMessage) => RouteAsync<SystemTimeMessage>(envelope, (ctc, m) => ctc.SetSystemTimeAsync(m.SystemTime, cancellationToken)),
            _ => null,
        };

        if (apply is null)
        {
            await ReportOnUiThreadAsync($"Ignored inbound message of unknown type '{envelope.MessageType}'.");
            return;
        }

        // CTC state backs WPF bindings, so it is only ever mutated on the UI thread. The
        // service call is awaited to completion (including any dispatch sends it makes), so
        // inbound messages are applied strictly one after another.
        await _dispatcher.InvokeAsync(async () =>
        {
            string status;
            try
            {
                await apply(_ctc);
                status = $"Received {envelope.MessageType}.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. a BlockStatusMessage for a block that is not in the current layout.
                status = $"Rejected {envelope.MessageType}: {ex.Message}";
            }

            Report(status);
        }).Task.Unwrap();
    }

    private static Func<ICTCService, Task> Route<TMessage>(MessageEnvelope envelope, Action<ICTCService, TMessage> apply)
        where TMessage : class
    {
        var message = MessageSerializer.DeserializePayload<TMessage>(envelope);
        return ctc =>
        {
            apply(ctc, message);
            return Task.CompletedTask;
        };
    }

    private static Func<ICTCService, Task> RouteAsync<TMessage>(MessageEnvelope envelope, Func<ICTCService, TMessage, Task> apply)
        where TMessage : class
    {
        var message = MessageSerializer.DeserializePayload<TMessage>(envelope);
        return ctc => apply(ctc, message);
    }

    private void ReportError(Exception ex) =>
        _ = ReportOnUiThreadAsync($"Inbound message error: {ex.Message}");

    private async Task ReportOnUiThreadAsync(string status) =>
        await _dispatcher.InvokeAsync(() => Report(status));

    private void Report(string status)
    {
        Debug.WriteLine($"[CTC inbound] {status}");
        StatusReported?.Invoke(this, status);
    }
}
