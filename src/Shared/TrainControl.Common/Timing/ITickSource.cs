namespace TrainControl.Common.Timing;

/// <summary>
/// Placeholder abstraction for "something that advances simulated time".
/// Left as an interface only — no implementation is chosen yet.
/// </summary>
public interface ITickSource
{
    TimeSpan Elapsed { get; }

    void Tick(TimeSpan delta);
}
