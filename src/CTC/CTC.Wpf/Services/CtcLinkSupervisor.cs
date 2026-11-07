using System.Diagnostics;
using System.Windows.Threading;
using CTC.Core.Interfaces;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Services;

/// <summary>
/// Keeps CTC's links to the live modules healthy, on wall time:
/// <list type="bullet">
///   <item>Until CTC has a track layout it asks the Track Model for one
///   (<see cref="TrackModelSnapshotRequestMessage"/>) every <see cref="LayoutRequestInterval"/>, so a
///   CTC started after the Track Model still loads the production layout. The Track Model also
///   pushes it on its own start-up and retries while CTC is offline.</item>
///   <item>Every <see cref="TickInterval"/> it lets CTC mark wayside statuses that stopped arriving
///   as stale and renew still-valid movement requests before the Track Controller's watchdog expires.</item>
/// </list>
/// </summary>
/// <remarks>
/// Infrastructure only: the decisions live in CTC.Core and run on the UI thread like every other
/// CTC state change. Wall time is used deliberately, independent of the simulation clock's speed or
/// pause, because the Track Controller's watchdogs run on wall time.
/// </remarks>
public sealed class CtcLinkSupervisor
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan LayoutRequestInterval = TimeSpan.FromSeconds(3);

    private readonly ICTCService _ctc;
    private readonly Dispatcher _dispatcher;
    private readonly string _trackModelPipe;

    /// <param name="trackModelPipe">Track Model inbound pipe; <see cref="NamedPipeNames.TrackModel"/> unless a test isolates it.</param>
    public CtcLinkSupervisor(ICTCService ctc, Dispatcher dispatcher, string? trackModelPipe = null)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _trackModelPipe = string.IsNullOrWhiteSpace(trackModelPipe) ? NamedPipeNames.TrackModel : trackModelPipe;
    }

    /// <summary>Readable link events (layout requests, housekeeping failures). Raised on the UI thread.</summary>
    public event EventHandler<string>? StatusReported;

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled; never throws for link failures.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Stopwatch? sinceLayoutRequest = null;
        bool? trackModelReachable = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool needsLayout = await _dispatcher.InvokeAsync(() => _ctc.State.Lines.Count == 0, DispatcherPriority.Normal, cancellationToken);
                if (needsLayout && (sinceLayoutRequest is null || sinceLayoutRequest.Elapsed >= LayoutRequestInterval))
                {
                    sinceLayoutRequest = Stopwatch.StartNew();
                    bool reachable = await RequestLayoutAsync(cancellationToken);
                    if (reachable != trackModelReachable)
                    {
                        trackModelReachable = reachable;
                        await ReportAsync(reachable
                            ? "Requested the track layout from Track Model."
                            : "Waiting for Track Model to publish the track layout (not reachable yet; retrying).");
                    }
                }

                await _dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        _ctc.RefreshBlockStatusFreshness();
                        await _ctc.SynchronizeMovementRequestsAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        StatusReported?.Invoke(this, $"Link housekeeping failed: {ex.Message}");
                    }
                }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();

                await Task.Delay(TickInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> RequestLayoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await NamedPipeTransport.SendAsync(_trackModelPipe, new TrackModelSnapshotRequestMessage(), cancellationToken, connectTimeoutMilliseconds: 250);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ReportAsync(string status) =>
        await _dispatcher.InvokeAsync(() => StatusReported?.Invoke(this, status));
}
