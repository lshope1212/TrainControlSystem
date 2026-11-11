namespace TrainControl.Tests.CTC;

/// <summary>Wall clock the test moves by hand, for freshness and renewal rules.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new DateTimeOffset(2026, 11, 7, 17, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
