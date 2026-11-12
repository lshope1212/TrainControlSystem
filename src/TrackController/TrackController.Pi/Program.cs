using System.Net;
using System.Net.Sockets;
using System.Text;
using TrackController.Runtime;
using TrainControl.Common.Communication;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("TrackController.Pi [--port 11405] [--bind 192.168.50.2] [--status-file status.json]\nConnect from Windows using the Pi IP and port. Use --bind 127.0.0.1 for local testing or an optional SSH tunnel. Production sessions evaluate automatically after configuration; fresh feedback and requests are required.");
    return;
}

int port = 11405;
IPAddress bind = IPAddress.Parse("192.168.50.2");
string statusFile = Path.Combine(AppContext.BaseDirectory, "status.json");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int selected) && selected is > 0 and <= 65535)
    {
        port = selected;
    }
    else if (args[i] == "--bind" && i + 1 < args.Length && IPAddress.TryParse(args[++i], out var address))
    {
        bind = address;
    }
    else if (args[i] == "--status-file" && i + 1 < args.Length)
    {
        statusFile = Path.GetFullPath(args[++i]);
    }
    else
    {
        Console.Error.WriteLine("Use --port NUMBER, --bind IP, and/or --status-file PATH. See --help.");
        Environment.ExitCode = 2;
        return;
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
await using var runtime = new PiRuntimeServer(port, bind);
runtime.StatusChanged += message => Console.WriteLine($"{DateTimeOffset.Now:T} {message}");
string? previousStatus = null;
runtime.SnapshotProduced += snapshot =>
{
    string state = snapshot.IsPaused ? "CONTROLLER NOT READY" : snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0) ? "MOVEMENT PERMITTED" : "STOP / NO AUTHORIZATION";
    string status = $"{snapshot.WaysideId}: {state}; fresh blocks {snapshot.Blocks.Count(blockStatus => blockStatus.IsFresh)}/{snapshot.Blocks.Count}; PLC {snapshot.PlcName}";
    if (status != previousStatus)
    {
        Console.WriteLine($"{DateTimeOffset.Now:T} {status}");
        previousStatus = status;
    }

    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusFile)!);
        File.WriteAllText(statusFile + ".tmp", MessageSerializer.Serialize(snapshot), new UTF8Encoding(false));
        File.Move(statusFile + ".tmp", statusFile, overwrite: true);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"LED status file unavailable: {ex.Message}");
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"LED status file unavailable: {ex.Message}");
    }
};
try
{
    runtime.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Cannot listen on {bind}:{port}: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
Console.WriteLine($"Pi controller listening on {bind}:{runtime.Port}. Status: {statusFile}");
Console.WriteLine("Waiting for the PC router to upload its assigned territory and its PLC program. Ctrl+C stops.");
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("TrackController.Pi: shutdown requested.");
}
