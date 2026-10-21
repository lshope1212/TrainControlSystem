using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrainControl.Contracts.Enums;

namespace TrackController.Tests;

/// <summary>Proves PLC-owned comparisons and interlocks separately from the independent output veto.</summary>
[TestClass]
public sealed class VitalPlcTests
{
    private static string Source => PiRuntimeTests.ProgramSource;

    private static Dictionary<string, bool[]> RouteInputs()
    {
        var inputs = VitalPlcProgram.BooleanInputs.ToDictionary(name => name, _ => new[] { false });
        foreach (string name in VitalPlcProgram.WordInputs)
        {
            inputs[name] = new bool[PlcBits.Width];
        }
        foreach (string name in new[] { "running", "request_fresh", "origin_occupied", "scan_live", "feedback_fresh", "block_clear", "is_origin" })
        {
            inputs[name] = [true];
        }
        inputs["suggested_speed"] = inputs["speed_in"] = PlcBits.Encode(12);
        inputs["suggested_authority"] = inputs["remaining_in"] = PlcBits.Encode(150);
        inputs["block_length"] = PlcBits.Encode(100);
        inputs["track_limit"] = PlcBits.Encode(8);
        return inputs;
    }

    [TestMethod]
    public void ExplicitBooleanGates_ExhaustiveEightBitInputs_CompareAddAndSubtract()
    {
        var plc = VitalPlcProgram.Compile(Source);
        var inputs = RouteInputs();
        var words = Enumerable.Range(0, 256).Select(value => Enumerable.Range(0, PlcBits.Width).Select(bit => bit < 8 && (value & (1 << bit)) != 0).ToArray()).ToArray();
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                inputs["distance_in"] = words[a];
                inputs["remaining_in"] = words[b];
                inputs["block_length"] = words[a];
                var result = plc.Execute(inputs);
                Assert.AreEqual((double)(a + Math.Min(a, b)), Math.Round(PlcBits.Decode(result["route_distance"]) * PlcBits.Scale));
                Assert.AreEqual((double)(b - Math.Min(a, b)), Math.Round(PlcBits.Decode(result["route_remaining"]) * PlcBits.Scale));
            }
        }
    }

    private static string ReplaceWord(string source, string name, bool[] bits)
    {
        for (int bit = 0; bit < bits.Length; bit++)
        {
            source = System.Text.RegularExpressions.Regex.Replace(source, $"^{name}_{bit:00} = [^\\r\\n]*", $"{name}_{bit:00} = {(bits[bit] ? "true" : "false")}", System.Text.RegularExpressions.RegexOptions.Multiline);
        }
        return source;
    }

    [TestMethod]
    public void Codec_RoundsConservativelyAndRejectsUnrepresentableValues()
    {
        foreach (double value in new[] { 0, double.Epsilon, 0.3048, 11.176, 365.76, Math.BitDecrement(1), 10_000_000.123456 })
        {
            double decoded = PlcBits.Decode(PlcBits.Encode(value));
            Assert.IsLessThanOrEqualTo(value, decoded);
            Assert.IsLessThanOrEqualTo(0.0000011, value - decoded);
        }
        foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            Assert.ThrowsExactly<ArgumentException>(() => PlcBits.Encode(value));
        }
    }

    [TestMethod]
    public void RouteScan_PlcSelectsSpeedAndAccumulatesAuthorityFromRawWords()
    {
        var plc = VitalPlcProgram.Compile(Source);
        var inputs = RouteInputs();
        var first = plc.Execute(inputs);
        Assert.AreEqual(8d, PlcBits.Decode(first["route_speed"]));
        Assert.AreEqual(100d, PlcBits.Decode(first["route_distance"]));
        Assert.AreEqual(50d, PlcBits.Decode(first["route_remaining"]));
        inputs["distance_in"] = first["route_distance"];
        inputs["remaining_in"] = first["route_remaining"];
        inputs["speed_in"] = first["route_speed"];
        inputs["track_limit"] = PlcBits.Encode(6);
        inputs["is_origin"] = [false];
        var second = plc.Execute(inputs);
        Assert.AreEqual(6d, PlcBits.Decode(second["route_speed"]));
        Assert.AreEqual(150d, PlcBits.Decode(second["route_distance"]));
        Assert.AreEqual(0d, PlcBits.Decode(second["route_remaining"]));
        Assert.IsFalse(second["route_continue"][0]);
        // Changing PLC comparator selection changes the proposed value before any C# guard.
        var altered = VitalPlcProgram.Compile(Source.Replace("track_is_slower = g_track_is_slower_047", "track_is_slower = false"));
        Assert.AreEqual(8d, PlcBits.Decode(altered.Execute(inputs)["route_speed"]));
    }

    [TestMethod]
    [DataRow("feedback_fresh", false)]
    [DataRow("block_faulted", true)]
    [DataRow("block_unknown", true)]
    [DataRow("request_fresh", false)]
    [DataRow("request_retired", true)]
    [DataRow("route_conflict", true)]
    [DataRow("maintenance", true)]
    [DataRow("running", false)]
    public void RawFacts_PlcItselfRejectsUnsafeRoute(string fact, bool value)
    {
        var inputs = RouteInputs();
        inputs[fact] = [value];
        var result = VitalPlcProgram.Compile(Source).Execute(inputs);
        Assert.IsTrue(result["route_fault"][0]);
        Assert.AreEqual(0d, PlcBits.Decode(result["route_distance"]));
        Assert.IsFalse(result["route_continue"][0]);
    }

    [TestMethod]
    public void EquipmentAndClosure_RawFactsDrivePlcOutputs()
    {
        var plc = VitalPlcProgram.Compile(Source);
        var inputs = RouteInputs();
        inputs["zone_ok_in"] = [true];
        inputs["switch_request_known"] = [true];
        inputs["reserved_known"] = [true];
        var clear = plc.Execute(inputs);
        Assert.IsTrue(clear["switch_move"][0]);
        Assert.IsTrue(clear["closure_closed"][0]);
        Assert.IsFalse(clear["crossing_close"][0]);
        inputs["reserved"] = [true];
        Assert.IsFalse(plc.Execute(inputs)["switch_move"][0]);
        inputs["reserved"] = [false];
        inputs["reserved_known"] = [false];
        Assert.IsFalse(plc.Execute(inputs)["switch_move"][0]);
        inputs["block_clear"] = [false];
        var occupied = plc.Execute(inputs);
        Assert.IsFalse(occupied["closure_closed"][0]);
        Assert.IsTrue(occupied["crossing_close"][0]);
        inputs["block_clear"] = [true];
        inputs["active_route"] = [true];
        Assert.IsTrue(plc.Execute(inputs)["crossing_close"][0]);
    }

    [TestMethod]
    public void SwitchDirectionAndOccupiedBlock_PlcControlsTraversalAndEndpoint()
    {
        var plc = VitalPlcProgram.Compile(Source);
        var inputs = RouteInputs();
        foreach (string name in new[] { "has_switch", "switch_known", "switch_confirmed", "has_next", "has_stem", "entry_stem", "exit_normal", "reported_reverse" })
        {
            inputs[name] = [true];
        }
        Assert.IsTrue(plc.Execute(inputs)["route_fault"][0]);
        inputs["reported_reverse"] = [false];
        inputs["reported_normal"] = [true];
        Assert.IsTrue(plc.Execute(inputs)["route_continue"][0]);
        inputs["is_origin"] = [false];
        inputs["block_clear"] = [false];
        inputs["distance_in"] = PlcBits.Encode(100);
        inputs["remaining_in"] = PlcBits.Encode(50);
        var obstruction = plc.Execute(inputs);
        Assert.IsFalse(obstruction["route_continue"][0]);
        Assert.IsFalse(obstruction["route_fault"][0]);
        Assert.AreEqual(100d, PlcBits.Decode(obstruction["route_distance"]));
    }

    [TestMethod]
    public void Overflow_PlcFaultsBeforeGrantingPermission()
    {
        var inputs = RouteInputs();
        inputs["distance_in"] = Enumerable.Repeat(true, PlcBits.Width).ToArray();
        var result = VitalPlcProgram.Compile(Source).Execute(inputs);
        Assert.IsTrue(result["route_fault"][0]);
        Assert.IsFalse(result["route_continue"][0]);
    }

    [TestMethod]
    public void ReplacementOutputWords_ControlCommands_AndUnsafeWordsAreVetoed()
    {
        foreach (double speed in new[] { 3d, 20d })
        {
            string changed = ReplaceWord(Source, "selected_speed", PlcBits.Encode(speed));
            var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
            controller.LoadPlc("replacement.plc", changed);
            controller.ApplyFeedback(PiRuntimeTests.Clear(controller.GetConfiguration()));
            controller.SetSimulation(new() { IsPaused = false });
            controller.SubmitMovement(PiRuntimeTests.Movement());
            var snapshot = controller.GetSnapshot();
            Assert.AreEqual(speed == 3 ? 3d : 0d, snapshot.Authorizations.Single().AuthorizedSpeedMetersPerSecond);
            if (speed == 20)
            {
                Assert.IsTrue(snapshot.Blocks.Where(block => block.HasSignal).All(block => block.CommandedSignal == SignalState.Red));
                StringAssert.Contains(snapshot.Authorizations.Single().Reason, "Independent safety check");
            }
        }
    }

    [TestMethod]
    public void UnknownSwitchWithoutRequest_RemainsUnknown()
    {
        var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
        controller.LoadPlc("showcase.plc", Source);
        var feedback = PiRuntimeTests.Clear(controller.GetConfiguration(), "none");
        feedback.Blocks.Single(block => block.BlockId == "6").Switch = SwitchPosition.Unknown;
        controller.ApplyFeedback(feedback);
        controller.SetSimulation(new() { IsPaused = false });
        Assert.AreEqual(SwitchPosition.Unknown, controller.GetSnapshot().Blocks.Single(block => block.BlockId == "6").CommandedSwitch);
    }

    [TestMethod]
    public void SmallestPositiveAuthority_StillReceivesIndependentSpeedCheck()
    {
        var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
        controller.LoadPlc("unsafe.plc", ReplaceWord(Source, "selected_speed", PlcBits.Encode(20)));
        controller.ApplyFeedback(PiRuntimeTests.Clear(controller.GetConfiguration()));
        controller.SetSimulation(new() { IsPaused = false });
        var request = PiRuntimeTests.Movement();
        request.SuggestedSpeedMetersPerSecond = 25;
        request.SuggestedAuthorityMeters = 0.000001;
        controller.SubmitMovement(request);
        Assert.AreEqual(0d, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
    }

    [TestMethod]
    public void ContradictoryOutputWords_AreRejected()
    {
        var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
        controller.LoadPlc("contradictory.plc", ReplaceWord(Source, "authorized_speed", new bool[PlcBits.Width]));
        controller.ApplyFeedback(PiRuntimeTests.Clear(controller.GetConfiguration()));
        controller.SetSimulation(new() { IsPaused = false });
        controller.SubmitMovement(PiRuntimeTests.Movement());
        var result = controller.GetSnapshot().Authorizations.Single();
        Assert.AreEqual(0d, result.AuthorizedAuthorityMeters);
        StringAssert.Contains(result.Reason, "Independent safety check");
    }

    [TestMethod]
    public void UnsafeOccupancyRule_IsRejectedByIndependentGuard()
    {
        var controller = new TrackControllerService(WaysideConfiguration.CreateDemo());
        controller.LoadPlc("unsafe.plc", Source.Replace("entry_allowed = not block_closed and (is_origin or (block_clear and not block_pending))", "entry_allowed = true"));
        var feedback = PiRuntimeTests.Clear(controller.GetConfiguration());
        feedback.Blocks.Single(block => block.BlockId == "7").Occupancy = OccupancyState.Occupied;
        controller.ApplyFeedback(feedback);
        controller.SetSimulation(new() { IsPaused = false });
        controller.SubmitMovement(PiRuntimeTests.Movement());
        Assert.AreEqual(0d, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
        StringAssert.Contains(controller.GetSnapshot().Authorizations.Single().Reason, "Independent safety check");
    }

    [TestMethod]
    public void InvalidProgramOrInput_IsRejectedInsteadOfUsingLegacyControl()
    {
        foreach (string source in new[]
        {
            "allow_route = true",
            Source.Replace("track_is_slower = g_track_is_slower_047", "track_is_slower = 2.5"),
            Source.Replace("track_is_slower = g_track_is_slower_047", "track_is_slower = less(running, speed_in)"),
            Source.Replace("route_distance_00 =", "missing_distance_bit ="),
            Source + "\nallow_route = true",
            Source.Replace("track_is_slower = g_track_is_slower_047", "track_is_slower = and(track_limit, speed_in)")
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => VitalPlcProgram.Compile(source));
        }
        var inputs = RouteInputs();
        inputs.Remove("track_limit");
        Assert.ThrowsExactly<ArgumentException>(() => VitalPlcProgram.Compile(Source).Execute(inputs));
    }
}
