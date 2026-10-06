using System.Text;
using TrackController.Runtime;
using TrainControl.Common.Communication;

if (args.Contains("--help"))
{
    Console.WriteLine("TrackController.Pi [--port 11405] [--status-file status.json]\nListens on 127.0.0.1 only. Connect from Windows using an SSH tunnel. Starts each controller session paused.");
    return;
}

int port = 11405;
string statusFile = Path.GetFullPath("status.json");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int selected) && selected is > 0 and <= 65535)
        port = selected;
    else if (args[i] == "--status-file" && i + 1 < args.Length)
        statusFile = Path.GetFullPath(args[++i]);
    else
        throw new ArgumentException("Use --port NUMBER and/or --status-file PATH. See --help.");
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
await using var runtime = new PiRuntimeServer(port);
runtime.StatusChanged += message => Console.WriteLine($"{DateTimeOffset.Now:T} {message}");
string? previousStatus = null;
runtime.SnapshotProduced += snapshot =>
{
    string state = snapshot.IsPaused ? "PAUSED" : snapshot.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0) ? "MOVEMENT PERMITTED" : "STOP / NO AUTHORIZATION";
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
runtime.Start();
Console.WriteLine($"Pi controller listening on 127.0.0.1:{runtime.Port}. Status: {statusFile}");
Console.WriteLine("Waiting for the PC router to upload its assigned territory and shared PLC program. Ctrl+C stops.");
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine("TrackController.Pi: shutdown requested.");
}
