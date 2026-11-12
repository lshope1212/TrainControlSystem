using System.Diagnostics;
using System.Text;

namespace TrackController.Core.Diagnostics;

/// <summary>
/// One development diagnostic: how often something happened and how long it took. Recording is two
/// <see cref="Stopwatch.GetTimestamp"/> reads and a few interlocked adds, so it is always on; nothing is
/// logged unless a developer asks for <see cref="PerformanceMetrics.Report"/>.
/// </summary>
public sealed class PerformanceMetric(string name)
{
    private long _count, _totalTicks, _maxTicks;

    public string Name { get; } = name;

    public long Count => Interlocked.Read(ref _count);

    public TimeSpan Total => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _totalTicks));

    public TimeSpan Max => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _maxTicks));

    public TimeSpan Average => Count == 0 ? TimeSpan.Zero : Total / Count;

    /// <summary>Records one occurrence that started at <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    public void RecordSince(long startTimestamp) => Record(Stopwatch.GetTimestamp() - startTimestamp);

    /// <summary>Records one occurrence of <paramref name="ticks"/> Stopwatch ticks.</summary>
    public void Record(long ticks)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalTicks, ticks);
        long max;
        while (ticks > (max = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, max) != max)
        {
        }
    }

    /// <summary>Counts one occurrence without a duration.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _totalTicks, 0);
        Interlocked.Exchange(ref _maxTicks, 0);
    }

    public override string ToString() => _totalTicks == 0 && _maxTicks == 0
        ? $"{Name}: {Count}"
        : $"{Name}: {Count} × avg {Average.TotalMilliseconds:0.000} ms, max {Max.TotalMilliseconds:0.000} ms, total {Total.TotalMilliseconds:0} ms";
}

/// <summary>
/// Process-wide development diagnostics for the Track Controller's evaluation pipeline. They measure;
/// they never influence a decision. Set <c>TRACKCONTROLLER_PERF=1</c> to have hosts print
/// <see cref="Report"/> periodically (see <see cref="ReportingEnabled"/>).
/// </summary>
public static class PerformanceMetrics
{
    /// <summary>Complete <c>Reevaluate</c> passes (one controller's full PLC/safety evaluation).</summary>
    public static readonly PerformanceMetric Evaluations = new("Controller evaluations");

    /// <summary>Snapshot requests answered from the current evaluation because no input, time threshold or setting changed.</summary>
    public static readonly PerformanceMetric ReusedEvaluations = new("Snapshot requests served without re-evaluation");

    /// <summary>Router polls of every territory (<c>PollAllAsync</c>).</summary>
    public static readonly PerformanceMetric RouterPolls = new("Router polls");

    /// <summary>Time a router caller waited for the router's operation semaphore.</summary>
    public static readonly PerformanceMetric RouterLockWaits = new("Router lock waits");

    /// <summary>Router handling of Track Model feedback (single or batched), lock wait excluded.</summary>
    public static readonly PerformanceMetric RouterFeedback = new("Router feedback messages");

    /// <summary>Router handling of CTC movement requests, lock wait excluded.</summary>
    public static readonly PerformanceMetric RouterMovements = new("Router movement messages");

    /// <summary>Router handling of every other message type, lock wait excluded.</summary>
    public static readonly PerformanceMetric RouterOther = new("Router other messages");

    /// <summary>Operator window refreshes (WPF host).</summary>
    public static readonly PerformanceMetric UiRefreshes = new("UI refreshes");

    private static readonly PerformanceMetric[] All = [Evaluations, ReusedEvaluations, RouterPolls, RouterLockWaits, RouterFeedback, RouterMovements, RouterOther, UiRefreshes];

    /// <summary>True when a developer set <c>TRACKCONTROLLER_PERF=1</c>; hosts then print <see cref="Report"/> periodically.</summary>
    public static bool ReportingEnabled { get; } = Environment.GetEnvironmentVariable("TRACKCONTROLLER_PERF") == "1";

    public static void Reset()
    {
        foreach (var metric in All) metric.Reset();
    }

    public static string Report()
    {
        var text = new StringBuilder("[Track Controller performance]");
        foreach (var metric in All) text.Append(Environment.NewLine).Append("  ").Append(metric);
        return text.ToString();
    }
}
