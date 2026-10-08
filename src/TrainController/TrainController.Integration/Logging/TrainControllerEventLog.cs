namespace TrainController.Integration.Logging;

public enum TrainControllerLogLevel
{
    /// <summary>Per-tick detail; only recorded when diagnostic logging is enabled.</summary>
    Diagnostic = 0,
    Info,
    Warning,
    Error
}

public sealed record TrainControllerLogEntry(
    DateTimeOffset Timestamp,
    TrainControllerLogLevel Level,
    string Category,
    string? TrainId,
    string Message);

/// <summary>Integration / safety event log for the Train Controller subsystem.</summary>
public interface ITrainControllerEventLog
{
    bool IsEnabled(TrainControllerLogLevel level);

    void Log(TrainControllerLogLevel level, string category, string? trainId, string message);
}

public sealed class NullTrainControllerEventLog : ITrainControllerEventLog
{
    public static NullTrainControllerEventLog Instance { get; } = new NullTrainControllerEventLog();

    public bool IsEnabled(TrainControllerLogLevel level) => false;

    public void Log(TrainControllerLogLevel level, string category, string? trainId, string message)
    {
    }
}

/// <summary>
/// Bounded in-memory log (newest last), suitable for display in the Main UI.
/// Thread-safe. <see cref="EntryAdded"/> is raised on the logging thread.
/// </summary>
public sealed class InMemoryTrainControllerEventLog : ITrainControllerEventLog
{
    private readonly object _gate = new object();
    private readonly Queue<TrainControllerLogEntry> _entries = new Queue<TrainControllerLogEntry>();
    private readonly int _capacity;

    public InMemoryTrainControllerEventLog(int capacity = 1000, TrainControllerLogLevel minimumLevel = TrainControllerLogLevel.Info)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        MinimumLevel = minimumLevel;
    }

    /// <summary>Set to <see cref="TrainControllerLogLevel.Diagnostic"/> to record per-tick detail.</summary>
    public TrainControllerLogLevel MinimumLevel { get; set; }

    public event EventHandler<TrainControllerLogEntry>? EntryAdded;

    public bool IsEnabled(TrainControllerLogLevel level) => level >= MinimumLevel;

    public IReadOnlyList<TrainControllerLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Log(TrainControllerLogLevel level, string category, string? trainId, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var entry = new TrainControllerLogEntry(DateTimeOffset.Now, level, category, trainId, message);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        System.Diagnostics.Debug.WriteLine($"[TrainController {level}] {category} {trainId}: {message}");
        EntryAdded?.Invoke(this, entry);
    }
}
