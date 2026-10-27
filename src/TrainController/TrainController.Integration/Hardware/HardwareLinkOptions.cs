using System.Text.Json;

namespace TrainController.Integration.Hardware;

/// <summary>
/// Windows -> Raspberry Pi link settings. Loaded from the "HardwareLink" section of the WPF
/// app's appsettings.json; no developer-specific address is hard-coded.
/// </summary>
public sealed record HardwareLinkOptions
{
    /// <summary>Pi host name or IP. Default loopback so the Pi program can run on the same PC for testing.</summary>
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5050;

    /// <summary>PROVISIONAL default (200 ms) — not a project requirement. Max wait for one response.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>PROVISIONAL default (500 ms). Max wait for a TCP connect + handshake.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>PROVISIONAL default (2 s). Minimum time between connection attempts while the Pi is unreachable.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(2);

    public static HardwareLinkOptions Default { get; } = new HardwareLinkOptions();

    public string Endpoint => $"{Host}:{Port}";

    /// <exception cref="ArgumentException">An option is unusable.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("HardwareLink Host is required.");
        }

        if (Port is <= 0 or > 65535)
        {
            throw new ArgumentException($"HardwareLink Port {Port} is out of range.");
        }

        if (RequestTimeout <= TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero || ReconnectInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("HardwareLink timeouts must be positive.");
        }
    }

    /// <summary>
    /// Reads the optional "HardwareLink" section of a JSON settings file:
    /// <c>{ "HardwareLink": { "Host": "192.168.50.2", "Port": 5050, "RequestTimeoutMs": 200, "ConnectTimeoutMs": 500, "ReconnectIntervalMs": 2000 } }</c>.
    /// Missing file or missing values fall back to the defaults.
    /// </summary>
    /// <exception cref="ArgumentException">The file exists but is invalid.</exception>
    public static HardwareLinkOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("HardwareLink", out var section))
            {
                return Default;
            }

            var options = new HardwareLinkOptions
            {
                Host = section.TryGetProperty("Host", out var host) ? host.GetString() ?? Default.Host : Default.Host,
                Port = section.TryGetProperty("Port", out var port) ? port.GetInt32() : Default.Port,
                RequestTimeout = Milliseconds(section, "RequestTimeoutMs", Default.RequestTimeout),
                ConnectTimeout = Milliseconds(section, "ConnectTimeoutMs", Default.ConnectTimeout),
                ReconnectInterval = Milliseconds(section, "ReconnectIntervalMs", Default.ReconnectInterval),
            };
            options.Validate();
            return options;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"Invalid HardwareLink settings in '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies optional per-run overrides <c>--pi-host &lt;host&gt;</c> and <c>--pi-port &lt;port&gt;</c>
    /// (e.g. <c>--pi-host 127.0.0.1</c> to test against the Pi program running on this PC)
    /// without editing appsettings.json. Unrelated arguments are ignored.
    /// </summary>
    /// <exception cref="ArgumentException">An override is present but invalid.</exception>
    public HardwareLinkOptions WithCommandLineOverrides(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return this;
        }

        var result = this;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pi-host":
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("--pi-host requires a host name or IP address.");
                    }

                    result = result with { Host = args[++i] };
                    break;

                case "--pi-port":
                    if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out var port))
                    {
                        throw new ArgumentException("--pi-port requires a port number.");
                    }

                    result = result with { Port = port };
                    i++;
                    break;
            }
        }

        result.Validate();
        return result;
    }

    private static TimeSpan Milliseconds(JsonElement section, string name, TimeSpan fallback) =>
        section.TryGetProperty(name, out var value) ? TimeSpan.FromMilliseconds(value.GetDouble()) : fallback;
}
