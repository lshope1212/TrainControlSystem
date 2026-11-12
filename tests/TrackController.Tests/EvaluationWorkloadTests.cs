using System.Collections.Concurrent;
using System.Diagnostics;
using TrackController.Contracts.Messages;
using TrackController.Core.Diagnostics;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>
/// The evaluation workload of the course deployment (16 waysides, three lines, bundled PLC) while one Green
/// Line train holds the Edgebrook route's first authority segment: Track Model feedback batches arrive and the
/// three publishers (CTC, Track Model, monitor) poll between them, as in the running system.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class EvaluationWorkloadTests
{
    private const string Course = "course-territories-all-pc.deployment.json";

    /// <summary>GREEN-63 (yard) through GREEN-99: the Edgebrook route's first authority segment.</summary>
    private static readonly string[] FirstSegment =
        [.. Enumerable.Range(63, 37).Select(n => $"GREEN-{n}")];

    internal static MovementSuggestionMessage FirstSegmentRequest(string id = "edgebrook-1") => new()
    {
        RequestId = id, TrainId = "001", TargetBlockId = FirstSegment[0], EntryBlockId = null,
        PathBlockIds = FirstSegment[1..].ToList(), SuggestedSpeedMetersPerSecond = 8, SuggestedAuthorityMeters = 2_500
    };

    internal static List<TrackModelBlockStateMessage> Feedback(WaysideConfigurationMessage line, string occupied, DateTimeOffset observed,
        IReadOnlyDictionary<string, WaysideBlockStatus>? commands = null) =>
        line.Blocks.Select(block =>
        {
            var command = commands?.GetValueOrDefault(block.BlockId);
            return new TrackModelBlockStateMessage
            {
                BlockId = block.BlockId, ObservedAtUtc = observed,
                Occupancy = block.BlockId == occupied ? OccupancyState.Occupied : OccupancyState.Clear,
                // The Track Model shows what it was commanded (as the integration stand-in does).
                Signal = command?.CommandedSignal is SignalState.Green or SignalState.Yellow or SignalState.SuperGreen ? command.CommandedSignal : SignalState.Red,
                Switch = command?.CommandedSwitch is SwitchPosition.Reverse ? SwitchPosition.Reverse : SwitchPosition.Normal,
                Crossing = CrossingState.Closed, IsReservedByCtc = false
            };
        }).ToList();

    internal static async Task<(TerritoryRuntimeRouter Router, PreparedDeployment Plan, WaysideConfigurationMessage Green)> StartCourseAsync(DeploymentFiles files)
    {
        var plan = files.Read(Course);
        var router = plan.CreateRuntime(automaticOperation: true);
        var now = DateTimeOffset.UtcNow;
        foreach (var line in plan.Territories.GroupBy(territory => territory.Configuration.LineName))
        {
            var receipt = await PiRuntimeTests.Send(router, Feedback(line.First().Configuration, "GREEN-63", now));
            Assert.IsTrue(receipt.IsAccepted, receipt.Detail);
        }

        var green = plan.Territories.First(territory => territory.Configuration.LineName == "Green Line").Configuration;
        return (router, plan, green);
    }

    [TestMethod]
    public async Task CourseDeployment_GreenTrainWorkload_EvaluatesOnlyWhatChanged()
    {
        using var files = new DeploymentFiles();
        var (router, _, green) = await StartCourseAsync(files);
        await using var _ = router;
        var receipt = await PiRuntimeTests.Send(router, FirstSegmentRequest());
        Assert.IsTrue(receipt.IsAccepted, receipt.Detail);

        PerformanceMetrics.Reset();
        const int Messages = 20;
        var wall = Stopwatch.StartNew();
        for (int i = 0; i < Messages; i++)
        {
            var commands = router.LatestSnapshot.Blocks.ToDictionary(block => block.BlockId);
            await PiRuntimeTests.Send(router, Feedback(green, "GREEN-63", DateTimeOffset.UtcNow, commands));
            // CTC, Track Model and monitor publishers.
            for (int poll = 0; poll < 3; poll++) await router.PollAsync();
        }

        wall.Stop();
        double perBatch = (double)PerformanceMetrics.Evaluations.Count / Messages;
        Console.WriteLine($"{Messages} feedback batches + {3 * Messages} publisher polls in {wall.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"Evaluations per feedback batch (incl. its polls): {perBatch:0.0}");
        Console.WriteLine(PerformanceMetrics.Report());
        var origin = router.LatestSnapshot.Blocks.Single(block => block.BlockId == "GREEN-63");
        Assert.IsGreaterThan(0, origin.AuthorizedAuthorityMeters, "The workload is a granted movement: " + origin.MovementReason);
        // Before evaluation reuse: 144 (every Green wayside twice per batch, then all 16 twice per router poll).
        // Now: the 8 Green waysides that received the batch, plus the once-a-second refresh of each territory.
        Assert.IsLessThanOrEqualTo(16.0, perBatch, "Unchanged territories and repeated polls cost no evaluation.");
    }

    /// <summary>The train runs through the first segment: CTC re-requests from each block it enters.</summary>
    [TestMethod]
    public async Task CourseDeployment_MovingGreenTrain_ReportsEvaluationCost()
    {
        using var files = new DeploymentFiles();
        var (router, _, green) = await StartCourseAsync(files);
        await using var _ = router;
        Assert.IsTrue((await PiRuntimeTests.Send(router, FirstSegmentRequest("move-0"))).IsAccepted);

        PerformanceMetrics.Reset();
        var wall = Stopwatch.StartNew();
        int messages = 0;
        for (int index = 1; index < 12; index++)
        {
            for (int step = 0; step < 2; step++)
            {
                // The front enters the next block, then the rear leaves the previous one.
                var commands = router.LatestSnapshot.Blocks.ToDictionary(block => block.BlockId);
                var batch = Feedback(green, FirstSegment[index], DateTimeOffset.UtcNow, commands);
                if (step == 0) batch.Single(block => block.BlockId == FirstSegment[index - 1]).Occupancy = OccupancyState.Occupied;
                Assert.IsTrue((await PiRuntimeTests.Send(router, batch)).IsAccepted);
                messages++;
                for (int poll = 0; poll < 3; poll++) await router.PollAsync();
            }

            var next = FirstSegmentRequest($"move-{index}");
            next.TargetBlockId = FirstSegment[index];
            next.EntryBlockId = FirstSegment[index - 1];
            next.PathBlockIds = FirstSegment[(index + 1)..].ToList();
            var receipt = await PiRuntimeTests.Send(router, next);
            Assert.IsTrue(receipt.IsAccepted, receipt.Detail);
            messages++;
            for (int poll = 0; poll < 3; poll++) await router.PollAsync();
        }

        wall.Stop();
        Console.WriteLine($"{messages} messages (feedback + movement) with 3 publisher polls each in {wall.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"Evaluations per message (incl. its polls): {(double)PerformanceMetrics.Evaluations.Count / messages:0.0}");
        Console.WriteLine(PerformanceMetrics.Report());
        var origin = router.LatestSnapshot.Blocks.Single(block => block.BlockId == FirstSegment[11]);
        Assert.IsGreaterThan(0, origin.AuthorizedAuthorityMeters, origin.MovementReason);
    }

    [TestMethod]
    public async Task RouterPolls_WithNothingChanged_ReuseEveryTerritorysEvaluation()
    {
        using var files = new DeploymentFiles();
        var (router, _, _) = await StartCourseAsync(files);
        await using var _ = router;
        Assert.IsTrue((await PiRuntimeTests.Send(router, FirstSegmentRequest())).IsAccepted);
        await router.PollAsync();
        var before = (await router.PollAsync()).Blocks.Where(block => block.Request is null).ToDictionary(block => block.BlockId);
        for (int poll = 0; poll < 10; poll++)
        {
            var snapshot = await router.PollAsync();
            Assert.IsGreaterThan(DateTimeOffset.UtcNow, snapshot.ValidUntilUtc, "Each poll publishes a current snapshot.");
            foreach (var block in snapshot.Blocks.Where(block => block.Request is null))
            {
                Assert.AreSame(before[block.BlockId], block, $"{block.BlockId}: its territory served its settled evaluation.");
            }
        }

        Assert.IsGreaterThan(0, router.LatestSnapshot.Blocks.Single(block => block.BlockId == "GREEN-63").AuthorizedAuthorityMeters);
    }

    /// <summary>
    /// Feedback, movement renewals and three publishers' polls concurrently: every published snapshot is one
    /// complete joint evaluation (all 241 blocks; the grant is the full issued limit, never part of it).
    /// </summary>
    [TestMethod]
    public async Task ConcurrentInputsAndPolls_PublishOnlyCompleteSnapshots()
    {
        using var files = new DeploymentFiles();
        var (router, _, green) = await StartCourseAsync(files);
        await using var _ = router;
        Assert.IsTrue((await PiRuntimeTests.Send(router, FirstSegmentRequest())).IsAccepted);
        await router.PollAsync();
        double issued = router.LatestSnapshot.Blocks.Single(block => block.BlockId == "GREEN-63").AuthorizedAuthorityMeters;
        Assert.IsGreaterThan(0, issued);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var snapshots = new ConcurrentBag<WaysideSnapshotMessage>();
        async Task Inputs()
        {
            while (!stop.IsCancellationRequested)
            {
                var commands = router.LatestSnapshot.Blocks.ToDictionary(block => block.BlockId);
                Assert.IsTrue((await PiRuntimeTests.Send(router, Feedback(green, "GREEN-63", DateTimeOffset.UtcNow, commands))).IsAccepted);
                Assert.IsTrue((await PiRuntimeTests.Send(router, FirstSegmentRequest())).IsAccepted);
            }
        }

        async Task Publisher()
        {
            while (!stop.IsCancellationRequested)
            {
                snapshots.Add(await router.PollAsync());
                snapshots.Add(router.LatestSnapshot);
                await Task.Yield();
            }
        }

        await Task.WhenAll(Task.Run(Inputs), Task.Run(Publisher), Task.Run(Publisher), Task.Run(Publisher));
        Assert.IsGreaterThan(50, snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            Assert.HasCount(241, snapshot.Blocks);
            Assert.IsGreaterThan(snapshot.EvaluatedAtUtc, snapshot.ValidUntilUtc);
            var origin = snapshot.Blocks.Single(block => block.BlockId == "GREEN-63");
            Assert.AreEqual(issued, origin.AuthorizedAuthorityMeters, 1e-6, origin.MovementReason);
            Assert.AreEqual(1, snapshot.Blocks.Count(block => block.Request is not null));
        }
    }
}
