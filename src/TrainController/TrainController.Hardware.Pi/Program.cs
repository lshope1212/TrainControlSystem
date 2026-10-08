using System.Net;
using TrainController.Hardware.Pi;

// Hardware Train Controller — Raspberry Pi entry point.
//
// Usage:  TrainController.Hardware.Pi [--port 5050] [--bind 0.0.0.0]
//
// Runs until Ctrl+C. Controls TRAIN-002/004/006/008/010 with independent per-train state.
// Each received request = exactly one controller evaluation; the Pi never ticks by itself.

var port = 5050;
var bind = IPAddress.Any;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536:
            port = p;
            i++;
            break;
        case "--bind" when i + 1 < args.Length && IPAddress.TryParse(args[i + 1], out var address):
            bind = address;
            i++;
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: TrainController.Hardware.Pi [--port 5050] [--bind 0.0.0.0]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown or invalid argument '{args[i]}'. Use --help.");
            return 2;
    }
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");

// One-time JIT / JSON warm-up BEFORE listening, so the first real request meets the timeout.
Log($"Warm-up complete in {HardwareRequestHandler.WarmUp().TotalMilliseconds:F0} ms.");

await using (var server = new HardwareControllerServer(bind, port, new HardwareRequestHandler(), Log))
{
    try
    {
        server.Start();
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        Console.Error.WriteLine($"Cannot listen on {bind}:{port}: {ex.Message}");
        return 1;
    }

    Log("Press Ctrl+C to stop.");
    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
    }

    Log("Stopping.");
}

return 0;
