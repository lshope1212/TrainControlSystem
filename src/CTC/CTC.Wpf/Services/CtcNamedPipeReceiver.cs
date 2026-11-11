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
/// module or CTC.TestUI.Wpf. A <see cref="SystemTimeMessage"/> (the shared clock's state,
/// about ten per second) is also surfaced through <see cref="SystemTimeReceived"/> for the
/// clock display, and is not reported as a status unless it fails; the same goes for the live
/// wayside's periodic block statuses. After each block status CTC brings its movement requests
/// in line with the (possibly new) train locations before the next message is read, so inbound
/// messages are still applied strictly one after another.
/// </remarks>
public sealed class CtcNamedPipeReceiver
{
    private readonly ICTCService _ctc;
    private readonly Dispatcher _dispatcher;

    private readonly string _pipeName;

    /// <param name="pipeName">Inbound pipe; <see cref="NamedPipeNames.Ctc"/> unless a test isolates it.</param>
    public CtcNamedPipeReceiver(ICTCService ctc, Dispatcher dispatcher, string? pipeName = null)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? NamedPipeNames.Ctc : pipeName;
    }

    /// <summary>
    /// Readable outcome of each received message (or receive error). Raised on the UI thread.
    /// </summary>
    public event EventHandler<string>? StatusReported;

    /// <summary>
    /// The shared clock's authoritative state, raised on the UI thread just before CTC applies
    /// its time, so the clock display always matches the time CTC is acting on.
    /// </summary>
    public event EventHandler<SystemTimeMessage>? SystemTimeReceived;

    /// <summary>Receives until <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        NamedPipeTransport.ListenAsync(
            _pipeName,
            envelope => HandleAsync(envelope, cancellationToken),
            ReportError,
            cancellationToken);

    private async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        // Deserialize on the background thread; only the service call runs on the UI thread.
        Func<ICTCService, Task>? apply = envelope.MessageType switch
        {
            nameof(BlockStatusMessage) => RouteAsync<BlockStatusMessage>(envelope, async (ctc, m) =>
            {
                ctc.ApplyBlockStatus(m);
                await ctc.SynchronizeMovementRequestsAsync(cancellationToken);
            }),
            nameof(BlockStatusBatchMessage) => RouteAsync<BlockStatusBatchMessage>(envelope, (ctc, m) => ApplyBlockStatusesAsync(ctc, m, cancellationToken)),
            nameof(TrackLayoutMessage) => Route<TrackLayoutMessage>(envelope, (ctc, m) => _layoutSummary = ctc.ApplyTrackLayout(m).Summary),
            nameof(RequestAcknowledgementMessage) => Route<RequestAcknowledgementMessage>(envelope, (ctc, m) => ctc.ApplyRequestAcknowledgement(m)),
            nameof(TicketSalesMessage) => Route<TicketSalesMessage>(envelope, (ctc, m) => ctc.ApplyTicketSales(m)),
            nameof(SystemTimeMessage) => RouteAsync<SystemTimeMessage>(envelope, (ctc, m) => ApplySystemTimeAsync(ctc, m, cancellationToken)),
            _ => null,
        };

        if (apply is null)
        {
            // The raw type name is kept here: it is what a developer needs to diagnose the sender.
            await ReportOnUiThreadAsync($"Ignored inbound message of unknown type '{envelope.MessageType}'.");
            return;
        }

        // CTC state backs WPF bindings, so it is only ever mutated on the UI thread. The
        // service call is awaited to completion (including any dispatch sends it makes), so
        // inbound messages are applied strictly one after another.
        var displayName = MessageDisplayNameFormatter.ToDisplayName(envelope.MessageType);
        await _dispatcher.InvokeAsync(async () =>
        {
            string status;
            try
            {
                await apply(_ctc);

                // Clock state and live wayside statuses arrive continuously; reporting each one
                // would hide every other status.
                if (envelope.MessageType == nameof(SystemTimeMessage) || IsLiveBlockStatus(envelope))
                {
                    return;
                }

                status = envelope.MessageType == nameof(TrackLayoutMessage) && _layoutSummary is { } summary
                    ? $"Received {displayName}. {summary}"
                    : $"Received {displayName}.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. a BlockStatusMessage for a block that is not in the current layout.
                status = $"Rejected {displayName}: {ex.Message}";
            }

            Report(status);
        }).Task.Unwrap();
    }

    // Only touched on the UI thread, between applying a layout and reporting it.
    private string? _layoutSummary;

    private static bool IsLiveBlockStatus(MessageEnvelope envelope) =>
        envelope.MessageType == nameof(BlockStatusBatchMessage)
        || (envelope.MessageType == nameof(BlockStatusMessage)
            && envelope.Payload.TryGetProperty("reportedAtUtc", out var reportedAt)
            && reportedAt.ValueKind == System.Text.Json.JsonValueKind.String);

    /// <summary>
    /// A wayside batch is applied exactly as its statuses would be one by one: each in order, each on
    /// its own (a status CTC cannot apply, e.g. for a block it does not know, is reported, never
    /// remapped, and does not stop the others). Movement requests are brought in line once, after
    /// the whole evaluation has been applied.
    /// </summary>
    private static async Task ApplyBlockStatusesAsync(ICTCService ctc, BlockStatusBatchMessage batch, CancellationToken cancellationToken)
    {
        var rejected = new List<string>();
        foreach (var status in batch.Statuses ?? [])
        {
            try
            {
                ctc.ApplyBlockStatus(status);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                rejected.Add(ex.Message);
            }
        }

        await ctc.SynchronizeMovementRequestsAsync(cancellationToken);
        if (rejected.Count > 0)
        {
            throw new InvalidOperationException($"{rejected.Count} of {batch.Statuses!.Count} statuses not applied: {string.Join(" ", rejected.Distinct().Take(3))}{(rejected.Count > 3 ? " …" : "")}");
        }
    }

    private Task ApplySystemTimeAsync(ICTCService ctc, SystemTimeMessage message, CancellationToken cancellationToken)
    {
        SystemTimeReceived?.Invoke(this, message);

        // CTC acts on the authoritative time only; speed and paused state are display concerns.
        return ctc.SetSystemTimeAsync(message.SystemTime, cancellationToken);
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
