using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TrainControl.Common.Communication;
using TrainController.Abstractions.Hardware;
using TrainController.Hardware.Pi;

namespace TrainControl.Tests.TrainController.Hardware;

internal enum PiFault
{
    None = 0,
    Delay,          // reply after DelayMs (to provoke a timeout)
    Garbage,        // reply with a frame that is not JSON
    Close,          // drop the connection instead of replying
    Mutate,         // alter the correct reply (wrong train / tick / version / id / type)
    Fragment,       // correct reply, written a few bytes at a time
}

internal sealed record PiScript(PiFault Fault, Func<HardwareEnvelope, HardwareEnvelope>? Mutate = null, int DelayMs = 0)
{
    public static PiScript Normal { get; } = new PiScript(PiFault.None);
}

/// <summary>
/// Loopback Pi for tests: answers with the REAL <see cref="HardwareRequestHandler"/>, optionally
/// injecting a fault per request, and records every request received (in order).
/// </summary>
internal sealed class ScriptedPiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private Task _loop = Task.CompletedTask;

    public HardwareRequestHandler Handler { get; } = new HardwareRequestHandler();

    /// <summary>Decides how to answer each request (default: correctly).</summary>
    public Func<HardwareEnvelope, PiScript> Script { get; set; } = _ => PiScript.Normal;

    public ConcurrentQueue<HardwareEnvelope> Received { get; } = new ConcurrentQueue<HardwareEnvelope>();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static ScriptedPiServer Start()
    {
        var server = new ScriptedPiServer();
        server._listener.Start();
        server._loop = server.AcceptAsync(server._stop.Token);
        return server;
    }

    public IEnumerable<HardwareEnvelope> ControllerRequests() =>
        Received.Where(e => e.Type == HardwareMessageType.ControllerRequest);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception)
        {
        }
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                return;
            }

            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var frame = await LengthPrefixedFraming.ReadFrameAsync(stream, token);
                    if (frame is null)
                    {
                        return;
                    }

                    var request = HardwareProtocol.Deserialize(frame);
                    Received.Enqueue(request);
                    var plan = Script(request);

                    if (plan.Fault == PiFault.Close)
                    {
                        return;
                    }

                    if (plan.Fault == PiFault.Garbage)
                    {
                        await LengthPrefixedFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("{ this is not json"), token);
                        continue;
                    }

                    var reply = Handler.Handle(request);
                    if (plan.Fault == PiFault.Mutate && plan.Mutate is not null)
                    {
                        reply = plan.Mutate(reply);
                    }

                    var payload = HardwareProtocol.Serialize(reply);

                    if (plan.Fault == PiFault.Delay)
                    {
                        await Task.Delay(plan.DelayMs, token);
                    }

                    if (plan.Fault == PiFault.Fragment)
                    {
                        var framed = new byte[4 + payload.Length];
                        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
                        payload.CopyTo(framed, 4);
                        for (var i = 0; i < framed.Length; i += 7)
                        {
                            await stream.WriteAsync(framed.AsMemory(i, Math.Min(7, framed.Length - i)), token);
                            await stream.FlushAsync(token);
                        }

                        continue;
                    }

                    await LengthPrefixedFraming.WriteFrameAsync(stream, payload, token);
                }
            }
            catch (Exception)
            {
                // Client went away / test finished.
            }
        }
    }
}
