using TrackController.Contracts.Messages;
using TrackController.Core.Diagnostics;
using TrainControl.Contracts.Enums;
using MovementResult = (TrainControl.Contracts.Messages.MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason);

namespace TrackController.Core.Services;

/// <summary>
/// Reuse of a settled evaluation by <see cref="GetSnapshot"/> when evaluating again could not change anything.
/// </summary>
/// <remarks>
/// <para>
/// Publishers, the router and the runtime each request a snapshot many times per second, mostly with nothing
/// changed. A full evaluation (PLC route scans, switch and crossing logic, every block's status) is a pure
/// function of three things: the inputs (feedback, requests, settings), the state an evaluation carries over to
/// the next one (commanded equipment, switch-throw timers, approach locks, issued block authority, the previous
/// grants), and which side of each time threshold the clock is on (feedback and request watchdogs, the switch
/// confirmation window, approach locks). So the previous evaluation is exactly what a new one would produce
/// when all three are unchanged:
/// </para>
/// <list type="bullet">
///   <item>Inputs: every public operation that can change one clears <see cref="_reusable"/> before it
///         changes anything, and evaluates afterwards as before.</item>
///   <item>Carried state: an evaluation is reusable only if it left that state exactly as it found it (it is a
///         fixed point). An evaluation that changed it, e.g. by commanding a signal, is always followed by
///         another real evaluation, just as repeated polling did before.</item>
///   <item>Time: reuse ends 1 ms before the earliest threshold any current feedback, request, staged successor,
///         switch throw or approach lock reaches, so an expiry is evaluated no later than it was before.</item>
/// </list>
/// <para>
/// A reused evaluation is re-stamped for the moment it is served: its validity is computed exactly as a new
/// evaluation's would be (1.5 s, capped by the feedback and request expiries of every granted authority). As
/// defense in depth a real evaluation still runs at least every <see cref="MaximumReuse"/>. Simulated time
/// (manual, non-automatic operation) is display-only and advances on a reused evaluation exactly as on a new one.
/// </para>
/// </remarks>
public sealed partial class TrackControllerService
{
    /// <summary>Longest a settled evaluation is served without a real evaluation, whatever the thresholds say.</summary>
    internal static readonly TimeSpan MaximumReuse = TimeSpan.FromSeconds(1);

    /// <summary>A snapshot's own validity interval when no granted authority expires sooner.</summary>
    private static readonly TimeSpan SnapshotLease = TimeSpan.FromSeconds(1.5);

    /// <summary>Reuse ends this much before a threshold, so rounding can never let a crossed threshold be reused.</summary>
    private static readonly TimeSpan ThresholdMargin = TimeSpan.FromMilliseconds(1);

    /// <summary>What one evaluation carries over to the next for one block.</summary>
    private readonly record struct CarriedBlockState(
        SignalState Signal, CrossingState Crossing, SwitchPosition Switch, SwitchPosition DesiredSwitch,
        DateTimeOffset? ThrowStartedAt, DateTimeOffset? ApproachLockedUntil, string SwitchStatus, BlockAuthority? Authority)
    {
        public static CarriedBlockState Of(Block block) => new(block.CommandedSignal, block.CommandedCrossing, block.CommandedSwitch,
            block.DesiredSwitch, block.SwitchThrowStartedAt, block.SwitchApproachLockedUntil, block.SwitchStatus, block.Authority);
    }

    /// <summary>The latest evaluation is settled and no input changed since; see the remarks above.</summary>
    private bool _reusable;

    /// <summary>The latest evaluation may be served until (excluding) this time.</summary>
    private DateTimeOffset _reuseUntil;

    /// <summary>Earliest feedback or request expiry among the latest evaluation's granted authorities.</summary>
    private DateTimeOffset _grantExpiry = DateTimeOffset.MaxValue;

    private CarriedBlockState[] _carriedBefore = [];

    /// <summary>Total real evaluations by this controller (development diagnostics and tests).</summary>
    internal long EvaluationCount { get; private set; }

    /// <summary>Tests only: false makes every snapshot request evaluate, the reference behavior reuse must match exactly.</summary>
    internal bool ReuseEvaluations { get; set; } = true;

    /// <summary>Every input changes through a public operation, which calls this first.</summary>
    private void InvalidateEvaluation() => _reusable = false;

    /// <summary>The latest evaluation, re-stamped for now, when it is still exactly what a new one would produce.</summary>
    private bool TryReuseEvaluation(DateTimeOffset now, out WaysideSnapshotMessage snapshot)
    {
        snapshot = _snapshot;
        if (!ReuseEvaluations || !_reusable || now >= _reuseUntil || now < _snapshot.EvaluatedAtUtc)
        {
            return false;
        }

        // Simulated time (manual, non-automatic operation only) advances exactly as an evaluation advances it.
        if (!_paused && !_configuration.AutomaticOperation)
        {
            _simulationSeconds += Math.Max(0, (now - _lastPulse).TotalSeconds) * _multiplier;
        }

        _lastPulse = now;
        var validUntil = now + SnapshotLease;
        snapshot = _snapshot = _snapshot with
        {
            EvaluatedAtUtc = now, ValidUntilUtc = _grantExpiry < validUntil ? _grantExpiry : validUntil, SimulationSeconds = _simulationSeconds
        };
        PerformanceMetrics.ReusedEvaluations.Increment();
        return true;
    }

    private void CaptureCarriedState()
    {
        if (_carriedBefore.Length != _blocks.Count)
        {
            _carriedBefore = new CarriedBlockState[_blocks.Count];
        }

        int index = 0;
        foreach (var block in _blocks.Values)
        {
            _carriedBefore[index++] = CarriedBlockState.Of(block);
        }
    }

    private bool CarriedStateUnchanged(int blockCount)
    {
        if (blockCount != _blocks.Count)
        {
            return false;
        }

        int index = 0;
        foreach (var block in _blocks.Values)
        {
            if (_carriedBefore[index++] != CarriedBlockState.Of(block))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameResults(List<MovementResult> before, List<MovementResult> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (int i = 0; i < before.Count; i++)
        {
            var (a, b) = (before[i], after[i]);
            if (!ReferenceEquals(a.Request, b.Request) || a.AuthorizedSpeedMetersPerSecond != b.AuthorizedSpeedMetersPerSecond
                || a.AuthorizedAuthorityMeters != b.AuthorizedAuthorityMeters || a.Status != b.Status || a.Reason != b.Reason)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The earliest time after <paramref name="evaluatedAt"/> at which a time-dependent check of the evaluation
    /// can change outcome, capped at <see cref="MaximumReuse"/>.
    /// </summary>
    private DateTimeOffset NextTimeDependentChange(DateTimeOffset evaluatedAt)
    {
        var next = evaluatedAt + MaximumReuse;
        void Consider(DateTimeOffset threshold)
        {
            var limit = threshold - ThresholdMargin;
            if (threshold > evaluatedAt && limit < next)
            {
                next = limit;
            }
        }

        var feedbackTimeout = TimeSpan.FromSeconds(_configuration.FeedbackTimeoutSeconds);
        var requestTimeout = TimeSpan.FromSeconds(_configuration.RequestTimeoutSeconds);
        var switchConfirmation = TimeSpan.FromSeconds(SwitchConfirmationSeconds);
        foreach (var block in _blocks.Values)
        {
            if (block.Feedback is { } feedback)
            {
                Consider(block.Received + feedbackTimeout);
                Consider(feedback.ObservedAtUtc + feedbackTimeout);
            }

            if (block.SwitchThrowStartedAt is { } started)
            {
                Consider(started + switchConfirmation);
            }

            if (block.SwitchApproachLockedUntil is { } until)
            {
                Consider(until);
            }
        }

        foreach (var request in _requests.Values)
        {
            Consider(request.Received + requestTimeout);
        }

        foreach (var staged in _staged.Values)
        {
            Consider(staged.Received + requestTimeout);
        }

        return next;
    }
}
