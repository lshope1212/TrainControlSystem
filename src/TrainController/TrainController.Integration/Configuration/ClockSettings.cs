using System.Text.Json;
using TrainControl.Common.Communication;

namespace TrainController.Integration.Configuration;

/// <summary>
/// "Clock" section of appsettings.json. <see cref="Host"/> = true: this process runs the global
/// tick clock (interim, until CTC hosts it). false: the clock is hosted elsewhere
/// (<see cref="HostPipe"/>) and this process is only a participant — no code change needed.
/// </summary>
public sealed record ClockSettings(bool Host, IReadOnlyList<string> Participants, string HostPipe, int TickTimeoutMilliseconds)
{
    public static ClockSettings Default { get; } =
        new ClockSettings(true, new[] { NamedPipeNames.TrainModel }, NamedPipeNames.Ctc, 2000);

    public static ClockSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return Default;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("Clock", out var clock))
        {
            return Default;
        }

        var host = clock.TryGetProperty("Host", out var h) ? h.GetBoolean() : Default.Host;
        var participants = clock.TryGetProperty("Participants", out var p)
            ? p.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
            : Default.Participants.ToArray();
        var hostPipe = clock.TryGetProperty("HostPipe", out var hp) ? hp.GetString() ?? Default.HostPipe : Default.HostPipe;
        var timeout = clock.TryGetProperty("TickTimeoutMilliseconds", out var t) ? t.GetInt32() : Default.TickTimeoutMilliseconds;
        return new ClockSettings(host, participants, hostPipe, Math.Max(100, timeout));
    }
}
