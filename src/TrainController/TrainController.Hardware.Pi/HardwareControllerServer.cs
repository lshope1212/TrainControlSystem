using System.Net;
using System.Net.Sockets;
using TrainControl.Common.Communication;
using TrainController.Abstractions.Hardware;

namespace TrainController.Hardware.Pi;

/// <summary>
/// TCP server on the Raspberry Pi. Waits for a framed request, handles it, replies, waits for
/// the next — it never advances simulation on its own. The Windows Hardware backend keeps
/// one long-lived connection; a new connection (after a reconnect) is accepted at any time.
/// </summary>
public sealed class HardwareControllerServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly HardwareRequestHandler _handler;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private Task _acceptLoop = Task.CompletedTask;

    public HardwareControllerServer(IPAddress bindAddress, int port, HardwareRequestHandler handler, Action<string>? log = null)
    {
        _listener = new TcpListener(bindAddress ?? throw new ArgumentNullException(nameof(bindAddress)), port);
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log ?? (_ => { });
    }

    public HardwareRequestHandler Handler => _handler;

    /// <summary>Actual listening port (useful when started on port 0 in tests).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _log($"Hardware Train Controller listening on {_listener.LocalEndpoint} (protocol v{HardwareProtocol.Version}); serving {string.Join(", ", _handler.ServedTrainIds)}.");
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log($"Accept failed: {ex.Message}");
                continue;
            }

            _ = ServeClientAsync(client, cancellationToken);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _log($"Windows host connected from {remote}.");

        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await LengthPrefixedFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                    {
                        break;
                    }

                    HardwareEnvelope reply;
                    try
                    {
                        reply = _handler.Handle(HardwareProtocol.Deserialize(frame));
                    }
                    catch (HardwareProtocolException ex)
                    {
                        reply = new HardwareEnvelope { Type = HardwareMessageType.ErrorResponse, RequestId = -1, TickId = -1, Error = ex.Message };
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = HardwareProtocol.Serialize(reply);
                    }
                    catch (HardwareProtocolException ex)
                    {
                        bytes = HardwareProtocol.Serialize(new HardwareEnvelope
                        {
                            Type = HardwareMessageType.ErrorResponse,
                            RequestId = reply.RequestId,
                            TrainId = reply.TrainId,
                            TickId = reply.TickId,
                            Error = $"Reply could not be serialized: {ex.Message}",
                        });
                    }

                    await LengthPrefixedFraming.WriteFrameAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _log($"Connection from {remote} ended: {ex.Message}");
            }
            return;
        }

        _log($"Windows host {remote} disconnected.");
    }
}
