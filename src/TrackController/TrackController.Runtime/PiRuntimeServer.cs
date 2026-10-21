using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>One controller owner per connection. Disconnect/timeout ends that session; reconnect starts paused.</summary>
public sealed class PiRuntimeServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _owner = new(1, 1);
    private readonly List<Task> _sessions = [];
    private Task? _accept;

    public event Action<string>? StatusChanged;

    public event Action<WaysideSnapshotMessage>? SnapshotProduced;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Creates a loopback listener for one active controller owner at a time.</summary>
    public PiRuntimeServer(int port = 11405) => _listener = new(IPAddress.Loopback, port);

    /// <summary>Starts accepting Pi sessions while retaining exclusive territory ownership.</summary>
    public void Start()
    {
        if (_accept is not null)
        {
            throw new InvalidOperationException("Pi listener already running.");
        }

        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_sessions)
                {
                    _sessions.RemoveAll(task => task.IsCompleted);
                    _sessions.Add(SessionAsync(socket));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task SessionAsync(TcpClient socket)
    {
        LocalWaysideRuntime? runtime = null;
        bool owns = false;
        using (socket)
        {
            using (var protocol = new PiProtocol(socket.GetStream()))
            {
                try
                {
                    owns = await _owner.WaitAsync(0, _stop.Token);
                    using var first = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    first.CancelAfter(3000);
                    if (!owns)
                    {
                        await protocol.WriteAsync(RuntimeMessages.CreateRejection(nameof(PiSessionStart), "Pi is already assigned to another controller session. Disconnect it before switching live/test deployments."), first.Token);
                        return;
                    }

                    socket.NoDelay = true;
                    await protocol.WriteAsync(new PiHello(PiProtocol.Version, DateTimeOffset.UtcNow), first.Token);
                    var start = await protocol.ReadAsync(first.Token);
                    if (start.MessageType != nameof(PiSessionStart))
                    {
                        throw new InvalidDataException("Pi session initialization required.");
                    }

                    var setup = MessageSerializer.DeserializePayload<PiSessionStart>(start);
                    var controller = new TrackControllerService(setup.Configuration);
                    controller.LoadPlc(setup.ProgramName, setup.ProgramSource);
                    runtime = new(controller);
                    await protocol.WriteAsync(new WaysideAcknowledgementMessage(true, "Pi controller initialized and paused. Send fresh feedback and resume explicitly.", nameof(PiSessionStart)), first.Token);
                    StatusChanged?.Invoke($"Connected: {setup.Configuration.WaysideId}, {TrackController.Core.Models.WaysideConfiguration.GetOwnedIds(setup.Configuration).Count} owned blocks, PLC {setup.ProgramName}");
                    SnapshotProduced?.Invoke(runtime.LatestSnapshot);
                    while (!_stop.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        idle.CancelAfter(3000);
                        var input = await protocol.ReadAsync(idle.Token);
                        object response;
                        if (input.MessageType is nameof(WaysideConfigurationMessage) or nameof(PiSessionStart))
                        {
                            response = RuntimeMessages.CreateRejection(input.MessageType, "Territory ownership is fixed for this connection. Load a deployment to start a new session.");
                        }
                        else if (input.MessageType == nameof(WaysideControllerModeMessage) && !MessageSerializer.DeserializePayload<WaysideControllerModeMessage>(input).IsHardwareMode)
                        {
                            response = RuntimeMessages.CreateRejection(input.MessageType, "The Pi runs its assigned PLC program. Software-only mode is available in the standalone controller.");
                        }
                        else
                        {
                            response = await runtime.ReceiveAsync(input, _stop.Token);
                        }

                        SnapshotProduced?.Invoke(runtime.LatestSnapshot);
                        await protocol.WriteAsync(response, idle.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException)
                {
                    if (!_stop.IsCancellationRequested)
                    {
                        StatusChanged?.Invoke($"Pi session ended: {ex.Message}");
                    }
                }
                finally
                {
                    if (runtime is not null)
                    {
                        await runtime.DisposeAsync();
                        SnapshotProduced?.Invoke(UnavailableOutput.CreateStopSnapshot(runtime.LatestSnapshot, "PC connection ended. Awaiting a new paused session."));
                    }

                    if (owns)
                    {
                        _owner.Release();
                    }
                }
            }
        }
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_accept is not null)
        {
            await _accept;
        }

        Task[] sessions;
        lock (_sessions)
        {
            sessions = _sessions.ToArray();
        }

        await Task.WhenAll(sessions);
        _stop.Dispose();
        _owner.Dispose();
    }
}
