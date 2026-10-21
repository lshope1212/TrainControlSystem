using System.Globalization;
using TrackController.TestUI.Wpf.ViewModels;
using TrackController.Tests;
using TrainControl.Contracts.Enums;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises simulated CTC progression through real module pipes and controller authorizations.</summary>
[TestClass]
[DoNotParallelize]
public sealed class MovementProgressTests
{
    private static async Task StartRouteAsync(UiTestSession session, bool occupiedNext = false, string? deploymentPath = null)
    {
        var inputs = session.Inputs;
        if (deploymentPath is null)
        {
            session.Controller.SelectedSetup = "Blue Line";
            await ExecuteAsync(session.Controller.LoadSetupCommand);
        }
        else
        {
            await session.Controller.LoadDeploymentAsync(deploymentPath);
        }
        await UntilAsync(() => inputs.Snapshot.LineName == "Blue Line" && inputs.Blocks.Count == 15);
        await ExecuteAsync(inputs.LoadScenarioCommand);
        if (occupiedNext)
        {
            SetOccupancy(inputs, "5", OccupancyState.Occupied);
        }

        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        await ExecuteAsync(session.Controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        inputs.RequestedPath = "5, 6, 7, 8";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations[0].AuthorizedAuthorityMeters > 0);
    }

    private static void SetOccupancy(MainWindowViewModel inputs, string blockId, OccupancyState occupancy)
    {
        inputs.FeedbackRows.Single(row => row.BlockId == blockId).Occupancy = occupancy;
    }

    private static async Task AdvanceAsync(MainWindowViewModel inputs, string previous, string next)
    {
        SetOccupancy(inputs, previous, OccupancyState.Clear);
        SetOccupancy(inputs, next, OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations[0].TargetBlockId == next);
    }

    /// <summary>Arrival ends the last movement without clearing destination occupancy or renewing the request.</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task ApplyFeedback_ArrivesAtFinalBlock_ClearsRequestAndKeepsDestinationOccupied(bool occupyFirst) => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        inputs.RequestedPath = "5";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().PathBlockIds.Count == 1);
        SetOccupancy(inputs, occupyFirst ? "5" : "4", occupyFirst ? OccupancyState.Occupied : OccupancyState.Clear);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == (occupyFirst ? "5" : "4")).Occupancy == (occupyFirst ? OccupancyState.Occupied : OccupancyState.Clear));
        Assert.HasCount(1, inputs.Snapshot.Authorizations, "Both parts of the arrival must be observed before clearing the request.");
        SetOccupancy(inputs, occupyFirst ? "4" : "5", occupyFirst ? OccupancyState.Clear : OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.Single(block => block.BlockId == "4").Occupancy == OccupancyState.Clear && inputs.Blocks.Single(block => block.BlockId == "5").Occupancy == OccupancyState.Occupied);
        await Task.Delay(750);
        Assert.HasCount(0, inputs.Snapshot.Authorizations, "Entering the final block should finish the request; the train must not have to leave its destination.");
        Assert.AreEqual("5", inputs.TargetBlock);
        Assert.AreEqual("", inputs.RequestedPath);
        Assert.AreEqual("0", inputs.RequestedAuthority);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
        StringAssert.Contains(inputs.MovementProgress, "Arrived at final block 5");
        Assert.AreEqual(OccupancyState.Occupied, inputs.FeedbackRows.Single(row => row.BlockId == "5").Occupancy);
        await Task.Delay(1250);
        Assert.HasCount(0, inputs.Snapshot.Authorizations, "Input refreshes must not recreate a finished request.");
        Assert.AreEqual(OccupancyState.Occupied, inputs.Blocks.Single(block => block.BlockId == "5").Occupancy);
    });

    /// <summary>Follows all five route blocks, preserving the endpoint and displaying real authorized values.</summary>
    [TestMethod]
    public Task ApplyFeedback_MultipleTransitions_UpdatesRouteAndAuthorizationWithoutExtendingEndpoint() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        var initial = inputs.Snapshot.Authorizations.Single();
        var requestIds = new HashSet<string> { initial.RequestId };
        Assert.AreEqual(200, initial.SuggestedAuthorityMeters);
        for (int target = 5; target < 8; target++)
        {
            await AdvanceAsync(inputs, (target - 1).ToString(), target.ToString());
            var result = inputs.Snapshot.Authorizations.Single();
            double remaining = 200 - (target - 4) * 50;
            Assert.IsTrue(requestIds.Add(result.RequestId), "Each new target needs a new request ID.");
            Assert.AreEqual((target - 1).ToString(), result.EntryBlockId);
            CollectionAssert.AreEqual(Enumerable.Range(target + 1, 8 - target).Select(block => block.ToString()).ToArray(), result.PathBlockIds.ToArray());
            Assert.AreEqual(remaining, result.SuggestedAuthorityMeters, 0.00000001);
            Assert.AreEqual(remaining, result.AuthorizedAuthorityMeters, 0.00000001);
            Assert.AreEqual(initial.SuggestedSpeedMetersPerSecond, result.AuthorizedSpeedMetersPerSecond);
            Assert.AreEqual("Approved", result.Status);
            Assert.AreEqual(result.TargetBlockId, inputs.TargetBlock);
            Assert.AreEqual(result.EntryBlockId, inputs.EntryBlock);
            Assert.AreEqual(string.Join(", ", result.PathBlockIds), inputs.RequestedPath);
            Assert.AreEqual(remaining / DisplayUnits.MetersPerFoot, double.Parse(inputs.RequestedAuthority, CultureInfo.CurrentCulture), 0.005);
            StringAssert.Contains(inputs.LastResult, $"Block {target}: Approved");
            StringAssert.Contains(inputs.CancelMovementLabel, $"block {target}");
            await UntilAsync(() => session.Controller.Snapshot.Authorizations.Any(authorization => authorization.RequestId == result.RequestId));
            Assert.AreEqual(result.AuthorizedAuthorityMeters, session.Controller.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters);
        }

        string finalId = inputs.Snapshot.Authorizations.Single().RequestId;
        await Task.Delay(1250);
        Assert.AreEqual(finalId, inputs.Snapshot.Authorizations.Single().RequestId, "Repeating unchanged inputs must not advance or recreate a request.");
        SetOccupancy(inputs, "7", OccupancyState.Clear);
        SetOccupancy(inputs, "8", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 0);
        Assert.AreEqual("0", inputs.RequestedAuthority);
        Assert.AreEqual("", inputs.RequestedPath);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
        StringAssert.Contains(inputs.MovementProgress, "Arrived at final block 8");
        Assert.AreEqual(OccupancyState.Occupied, inputs.Blocks.Single(block => block.BlockId == "8").Occupancy);
        await UntilAsync(() => session.Controller.Snapshot.Authorizations.Count == 0);
    });

    /// <summary>Uses each actual block length, excludes the destination length and avoids display-rounding drift.</summary>
    [TestMethod]
    public Task ApplyFeedback_UnequalBlockLengths_CountsDownToDestinationEntryExactly() => RunAsync(async session =>
    {
        using var files = new DeploymentFiles();
        files.Edit("blue-line-track-map.json", layout =>
        {
            var lengths = new Dictionary<string, double> { ["4"] = 60.25, ["5"] = 90.125, ["6"] = 40.75, ["7"] = 110.375, ["8"] = 999 };
            foreach (var block in layout["blocks"]!.AsArray())
            {
                if (lengths.TryGetValue(block!["blockId"]!.GetValue<string>(), out double length))
                {
                    block["lengthMeters"] = length;
                }
            }
        });
        await StartRouteAsync(session, deploymentPath: files.PathFor("blue-line-all-pc.deployment.json"));
        var inputs = session.Inputs;
        Assert.AreEqual(301.5, inputs.Snapshot.Authorizations.Single().SuggestedAuthorityMeters);
        foreach (var step in new[] { (Previous: "4", Next: "5", Remaining: 241.25), (Previous: "5", Next: "6", Remaining: 151.125), (Previous: "6", Next: "7", Remaining: 110.375) })
        {
            await AdvanceAsync(inputs, step.Previous, step.Next);
            var result = inputs.Snapshot.Authorizations.Single();
            Assert.AreEqual(step.Remaining, result.SuggestedAuthorityMeters, 0.00000001);
            Assert.AreEqual(step.Remaining, result.AuthorizedAuthorityMeters, 0.00000001);
        }

        SetOccupancy(inputs, "7", OccupancyState.Clear);
        SetOccupancy(inputs, "8", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 0);
        Assert.AreEqual("0", inputs.RequestedAuthority);
    });

    /// <summary>Waits for both occupancy changes and supports either order, including a gap between sends.</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task ApplyFeedback_TransitionSentInEitherOrder_AdvancesOnlyAfterBothChanges(bool occupyFirst) => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        string originalId = inputs.Snapshot.Authorizations.Single().RequestId;
        SetOccupancy(inputs, occupyFirst ? "5" : "4", occupyFirst ? OccupancyState.Occupied : OccupancyState.Clear);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters < 200);
        await Task.Delay(1250);
        Assert.AreEqual("4", inputs.TargetBlock);
        Assert.AreEqual(originalId, inputs.Snapshot.Authorizations.Single().RequestId);
        Assert.DoesNotContain("target block cleared", inputs.LastReceipt, "The repeat loop must not resend a retired request during an occupancy gap.");
        SetOccupancy(inputs, occupyFirst ? "4" : "5", occupyFirst ? OccupancyState.Clear : OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().TargetBlockId == "5");
        Assert.AreEqual("Approved", inputs.Snapshot.Authorizations.Single().Status);
    });

    /// <summary>Requires an explicit feedback send and does not transmit unrelated movement drafts during progression.</summary>
    [TestMethod]
    public Task ApplyFeedback_UnsentEdits_UsesLastSentRouteAndSpeed() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        var original = inputs.Snapshot.Authorizations.Single();
        inputs.TargetBlock = "14";
        inputs.RequestedPath = "15";
        inputs.SuggestedSpeed = "70";
        SetOccupancy(inputs, "4", OccupancyState.Clear);
        SetOccupancy(inputs, "5", OccupancyState.Occupied);
        await Task.Delay(1250);
        Assert.AreEqual(original.RequestId, inputs.Snapshot.Authorizations.Single().RequestId);
        Assert.AreEqual(OccupancyState.Occupied, inputs.Blocks.Single(block => block.BlockId == "4").Occupancy);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().TargetBlockId == "5");
        Assert.AreEqual("6, 7, 8", inputs.RequestedPath);
        Assert.AreEqual("25", inputs.SuggestedSpeed);
        Assert.AreEqual(original.SuggestedAuthorityMeters - 50, inputs.Snapshot.Authorizations.Single().SuggestedAuthorityMeters);
    });

    /// <summary>Does not mistake an existing obstacle for the tracked test train.</summary>
    [TestMethod]
    public Task ApplyFeedback_NextBlockAlreadyOccupied_DoesNotAdvance() => RunAsync(async session =>
    {
        await StartRouteAsync(session, occupiedNext: true);
        var inputs = session.Inputs;
        string originalId = inputs.Snapshot.Authorizations.Single().RequestId;
        SetOccupancy(inputs, "4", OccupancyState.Clear);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        Assert.AreEqual(originalId, inputs.Snapshot.Authorizations.Single().RequestId);
        Assert.AreEqual("4", inputs.TargetBlock);
        StringAssert.Contains(inputs.MovementProgress, "not observed clear");
    });

    /// <summary>Skips and unknown feedback cannot silently change the origin.</summary>
    [TestMethod]
    public Task ApplyFeedback_SkippedBlockOrUnknownOrigin_DoesNotAdvance() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        SetOccupancy(inputs, "4", OccupancyState.Clear);
        SetOccupancy(inputs, "6", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        Assert.AreEqual("4", inputs.TargetBlock);
        StringAssert.Contains(inputs.MovementProgress, "no block is skipped");
        SetOccupancy(inputs, "4", OccupancyState.Unknown);
        SetOccupancy(inputs, "5", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().AuthorizedAuthorityMeters == 0);
        Assert.AreEqual("4", inputs.TargetBlock);
    });

    /// <summary>Advances the simulated CTC intent, but leaves fault handling to the controller.</summary>
    [TestMethod]
    public Task ApplyFeedback_TransitionIntoFault_WithholdsActualAuthorization() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        inputs.ShouldRepeatInputs = false;
        inputs.FeedbackRows.Single(row => row.BlockId == "5").IsFaulted = true;
        await AdvanceAsync(inputs, "4", "5");
        var result = inputs.Snapshot.Authorizations.Single();
        Assert.IsGreaterThan(0, result.SuggestedAuthorityMeters);
        Assert.AreEqual(0, result.AuthorizedAuthorityMeters);
        Assert.AreEqual(0, result.AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual("Withheld", result.Status);
        StringAssert.Contains(inputs.LastResult, "fault");
        inputs.FeedbackRows.Single(row => row.BlockId == "5").IsFaulted = false;
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1 && inputs.Snapshot.Authorizations.Single().Status == "Approved");
        Assert.AreEqual(result.RequestId, inputs.Snapshot.Authorizations.Single().RequestId);
    });

    /// <summary>Calculates draft authority from the selected route and rejects invalid topology before sending.</summary>
    [TestMethod]
    public Task RequestedPath_ChangedOrInvalid_RecalculatesAuthorityWithoutSendingDrafts() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        string originalId = inputs.Snapshot.Authorizations.Single().RequestId;
        Assert.AreEqual(200 / DisplayUnits.MetersPerFoot, double.Parse(inputs.RequestedAuthority, CultureInfo.CurrentCulture), 0.005);
        inputs.RequestedPath = "5, 6";
        Assert.AreEqual(100 / DisplayUnits.MetersPerFoot, double.Parse(inputs.RequestedAuthority, CultureInfo.CurrentCulture), 0.005);
        inputs.TargetBlock = "5";
        inputs.RequestedPath = "6";
        Assert.AreEqual(50 / DisplayUnits.MetersPerFoot, double.Parse(inputs.RequestedAuthority, CultureInfo.CurrentCulture), 0.005);
        inputs.RequestedPath = "";
        Assert.AreEqual("0", inputs.RequestedAuthority);
        foreach (string invalid in new[] { "999", "5, 6", "8" })
        {
            inputs.RequestedPath = invalid;
            Assert.AreEqual("—", inputs.RequestedAuthority);
            await ExecuteAsync(inputs.SendMovementCommand);
            StringAssert.Contains(inputs.Status, "connected route");
            Assert.AreEqual(originalId, inputs.Snapshot.Authorizations.Single().RequestId);
        }
    });

    /// <summary>Cancelling the progressed request also stops future automatic movement updates.</summary>
    [TestMethod]
    public Task CancelMovement_AfterProgression_StopsUpdatesDespiteFurtherOccupancy() => RunAsync(async session =>
    {
        await StartRouteAsync(session);
        var inputs = session.Inputs;
        await AdvanceAsync(inputs, "4", "5");
        inputs.TargetBlock = "14";
        await ExecuteAsync(inputs.CancelMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 0);
        SetOccupancy(inputs, "5", OccupancyState.Clear);
        SetOccupancy(inputs, "6", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await Task.Delay(1250);
        Assert.HasCount(0, inputs.Snapshot.Authorizations);
        Assert.IsFalse(inputs.CancelMovementCommand.CanExecute(null));
        StringAssert.Contains(inputs.MovementProgress, "Movement cancelled");
    });

    /// <summary>Uses loaded Green Line lengths and leaves movements for other targets untouched.</summary>
    [TestMethod]
    public Task ApplyFeedback_GreenLineWithAnotherRequest_UsesActualLengthAndPreservesOtherMovement() => RunAsync(async session =>
    {
        var inputs = session.Inputs;
        session.Controller.IsHardwareMode = false;
        await UntilAsync(() => !inputs.Snapshot.IsHardwareMode);
        await ExecuteAsync(inputs.LoadScenarioCommand);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Blocks.All(block => block.IsFresh));
        await ExecuteAsync(session.Controller.PauseResumeCommand);
        await UntilAsync(() => !inputs.Snapshot.IsPaused);
        inputs.TargetBlock = "12";
        inputs.RequestedPath = "";
        inputs.TrainLabel = "Other train";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1);
        string unrelatedId = inputs.Snapshot.Authorizations.Single().RequestId;
        inputs.TargetBlock = "5";
        inputs.RequestedPath = "6, 7, 8";
        inputs.TrainLabel = "Train 101";
        await ExecuteAsync(inputs.SendMovementCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 2);
        double originalLimit = inputs.Snapshot.Authorizations.Single(request => request.TargetBlockId == "5").SuggestedAuthorityMeters;
        double blockLength = inputs.Blocks.Single(block => block.BlockId == "5").LengthMeters;
        SetOccupancy(inputs, "5", OccupancyState.Clear);
        SetOccupancy(inputs, "6", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Any(request => request.TargetBlockId == "6"));
        Assert.HasCount(2, inputs.Snapshot.Authorizations);
        Assert.IsTrue(inputs.Snapshot.Authorizations.Any(request => request.RequestId == unrelatedId));
        var result = inputs.Snapshot.Authorizations.Single(request => request.TargetBlockId == "6");
        Assert.AreEqual(originalLimit - blockLength, result.SuggestedAuthorityMeters, 0.00000001);
        Assert.AreEqual(result.SuggestedAuthorityMeters, result.AuthorizedAuthorityMeters);
        StringAssert.Contains(inputs.LastResult, "Block 6: Approved");
        SetOccupancy(inputs, "6", OccupancyState.Clear);
        SetOccupancy(inputs, "7", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Any(request => request.TargetBlockId == "7"));
        SetOccupancy(inputs, "7", OccupancyState.Clear);
        SetOccupancy(inputs, "8", OccupancyState.Occupied);
        await ExecuteAsync(inputs.ApplyFeedbackCommand);
        await UntilAsync(() => inputs.Snapshot.Authorizations.Count == 1);
        Assert.AreEqual(unrelatedId, inputs.Snapshot.Authorizations.Single().RequestId);
        StringAssert.Contains(inputs.MovementProgress, "Arrived at final block 8");
    });
}
