using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainControl.Common.Utilities;
using TrainController.Integration.Hardware;
using TrainController.Integration.Presentation;
using TrainController.Integration.State;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class PresentationTests
{
    [TestMethod]
    public void DisplayUnits_ShowImperial()
    {
        Assert.AreEqual("43.5 mph", DisplayUnits.Speed(VehicleSpecification.Flexity2Blackpool.MaxSpeedMetersPerSecond));
        Assert.AreEqual("1,200 ft", DisplayUnits.Distance(365.76));
        Assert.AreEqual("643.7 hp", DisplayUnits.Power(480_000.0));
        Assert.AreEqual("69.8 °F", DisplayUnits.Temperature(21.0));
        Assert.AreEqual(DisplayUnits.Missing, DisplayUnits.Speed(null));
        Assert.AreEqual(DisplayUnits.Missing, DisplayUnits.Distance(null));
        Assert.AreEqual("00:01:05.5", DisplayUnits.SimulationTime(65.5));
    }

    [TestMethod]
    public void DisplayUnits_ImperialInputRoundTripsToSi()
    {
        Assert.AreEqual(22.0, DisplayUnits.ToMph(DisplayUnits.FromMph(22.0)), 1e-9);
        Assert.AreEqual(365.76, DisplayUnits.FromFeet(1200.0), 1e-9);
        Assert.AreEqual(21.0, DisplayUnits.FromFahrenheit(69.8), 1e-9);
        Assert.IsTrue(DisplayUnits.TryParseNumber("1.5", out var parsed));
        Assert.AreEqual(1.5, parsed);
        Assert.IsFalse(DisplayUnits.TryParseNumber("abc", out _));
        Assert.IsFalse(DisplayUnits.TryParseNumber("NaN", out _));
        Assert.IsFalse(DisplayUnits.TryParseNumber("", out _));
    }

    [TestMethod]
    public void MainStatus_ShowsFixedControllerSource_AndPiLinkForHardwareOnly()
    {
        var registry = new TrainStateRegistry();

        var software = TrainControllerPresenter.BuildMain(registry.Get("TRAIN-001"), HardwareConnectionState.Disconnected);
        Assert.AreEqual("SOFTWARE CONTROLLED", software.ControllerSource);
        Assert.IsFalse(software.IsHardware);
        Assert.AreEqual("Not applicable", software.HardwareLink.Text);

        var hardware = TrainControllerPresenter.BuildMain(registry.Get("TRAIN-002"), HardwareConnectionState.Disconnected);
        Assert.AreEqual("HARDWARE CONTROLLED", hardware.ControllerSource);
        Assert.IsTrue(hardware.IsHardware);
        Assert.AreEqual("Disconnected", hardware.HardwareLink.Text);
        Assert.AreEqual(DisplayTone.Danger, hardware.HardwareLink.Tone);

        Assert.AreEqual("Active", TrainControllerPresenter.HardwareLinkText(HardwareConnectionState.Active).Text);
        Assert.AreEqual("Ready", TrainControllerPresenter.HardwareLinkText(HardwareConnectionState.Ready).Text);
        Assert.AreEqual("Connecting", TrainControllerPresenter.HardwareLinkText(HardwareConnectionState.Connecting).Text);
        Assert.AreEqual("Faulted", TrainControllerPresenter.HardwareLinkText(HardwareConnectionState.Faulted).Text);
    }

    [TestMethod]
    public void MainStatus_WarnsAboutUntunedGains_UntilTuned()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-003");

        Assert.AreNotEqual(string.Empty, TrainControllerPresenter.BuildMain(slot, null).GainsWarning);

        slot.SetEngineerSettings(15_000.0, 200.0);
        Assert.AreEqual(string.Empty, TrainControllerPresenter.BuildMain(slot, null).GainsWarning);
    }

    [TestMethod]
    public void MainStatus_ConvertsControllerOutputToImperial()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-001");
        var model = new TrainModelInput { IsActive = true, TrackSignalValid = true, ActualSpeedMetersPerSecond = 10.0, AuthorizedSpeedMetersPerSecond = 15.0, RemainingAuthorityMeters = 365.76 };
        slot.RecordTickResult(model, new TrainControllerOutput
        {
            TrainId = "TRAIN-001",
            TickId = 4,
            Commands = new TrainModelCommand { PowerCommandWatts = 120_000.0, CabinTemperatureSetpointCelsius = 21.0 },
            Display = new DriverDisplayState
            {
                RemainingAuthorityMeters = 365.76,
                DistanceToNextStationMeters = 304.8,
                DistanceToStationBrakePointMeters = -1.0,
                DistanceToAuthorityBrakePointMeters = 304.8,
                NextStationName = "PIONEER",
                StationBrakingAdvised = true,
            },
        });

        var status = TrainControllerPresenter.BuildMain(slot, null);

        Assert.AreEqual("22.4 mph", status.ActualSpeed.Text);
        Assert.AreEqual("33.6 mph", status.AuthorizedSpeed.Text);
        Assert.AreEqual("1,200 ft", status.RemainingAuthority.Text);
        Assert.AreEqual("1,000 ft", status.DistanceToStation.Text);
        Assert.AreEqual("160.9 hp", status.CommandedPower.Text);
        Assert.AreEqual("Now", status.BrakePoint.Text);
        Assert.AreEqual(DisplayTone.Warning, status.StationGuidance.Tone);
        Assert.IsTrue(status.Guidance.HasData);
        Assert.AreEqual(1200.0, status.Guidance.AuthorityEndFeet!.Value, 1e-6);
        Assert.AreEqual(1000.0, status.Guidance.StationFeet!.Value, 1e-6);
        Assert.AreEqual(0.0, status.Guidance.BrakePointFeet!.Value, "A brake point already passed is drawn at the train.");
        Assert.IsTrue(status.Guidance.BrakingDue);
        Assert.AreEqual(1000.0, status.Guidance.AuthorityBrakePointFeet!.Value, 1e-6);
        Assert.IsFalse(status.Guidance.AuthorityBrakingDue);
    }

    [TestMethod]
    public void TestOutputs_ShowOnlyTrainModelFacingCommands()
    {
        var expected = new[]
        {
            "HasOutput", "TickText", "PowerCommand", "ServiceBrakeCommand", "EmergencyBrakeCommand", "LeftDoorCommand",
            "RightDoorCommand", "ExteriorLightCommand", "CabinTemperatureSetpoint", "StationAnnouncement", "LastAnnouncementSent",
        };

        var actual = typeof(TestUiOutputs).GetProperties()
            .Where(p => p.DeclaringType == typeof(TestUiOutputs) && p.Name != "EqualityContract")
            .Select(p => p.Name).ToArray();

        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    public void TestOutputs_FormatFailSafeInImperial()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-002");
        var failSafe = FailSafeOutput.Create("TRAIN-002", 3, EmergencyBrakeCause.HardwareCommunication, "Pi down", new TrainModelInput(), ControllerPolicy.Default);
        slot.RecordTickResult(new TrainModelInput(), failSafe);

        var outputs = TrainControllerPresenter.BuildTestOutputs(slot);

        Assert.IsTrue(outputs.HasOutput);
        Assert.AreEqual("Tick 3", outputs.TickText);
        Assert.AreEqual("0.0 hp", outputs.PowerCommand.Text);
        Assert.AreEqual("On", outputs.EmergencyBrakeCommand.Text);
        Assert.AreEqual("32.0 °F", outputs.CabinTemperatureSetpoint.Text);
    }

    [TestMethod]
    public void TestInputWarnings_FlagEdgeValuesWithoutBlockingThem()
    {
        var vehicle = VehicleSpecification.Flexity2Blackpool;

        Assert.IsEmpty(TrainControllerPresenter.TestInputWarnings(new TrainModelInput { TrackSignalValid = true }, vehicle));

        var warnings = TrainControllerPresenter.TestInputWarnings(
            new TrainModelInput { TrackSignalValid = true, ActualSpeedMetersPerSecond = 25.0, PassengerEmergencyBrakeRequested = true }, vehicle);
        Assert.IsTrue(warnings.Any(w => w.Contains("above the nominal vehicle maximum") && w.Contains("43.5 mph")));
        Assert.IsTrue(warnings.Any(w => w.Contains("Passenger emergency brake")));
    }

    [TestMethod]
    public void Power_IsShownInHorsepower_480kWIsAbout644hp()
    {
        Assert.AreEqual(644.0, Math.Round(UnitConversion.WattsToHorsepower(480_000.0)));
        Assert.AreEqual(480_000.0, UnitConversion.HorsepowerToWatts(UnitConversion.WattsToHorsepower(480_000.0)), 1e-6);
        Assert.AreEqual("643.7 hp", DisplayUnits.Power(VehicleSpecification.Flexity2Blackpool.TotalRatedPowerWatts));
    }

    [TestMethod]
    public void RequestedSpeedEntry_ParsesMph_ToMetersPerSecond()
    {
        var vehicle = VehicleSpecification.Flexity2Blackpool;

        var ok = RequestedSpeedEntry.Parse("30.0", vehicle);
        Assert.IsTrue(ok.IsAccepted);
        Assert.AreEqual(13.4112, ok.MetersPerSecond, 1e-9);
        Assert.AreEqual(DisplayTone.Good, ok.Message.Tone);

        var decimalEntry = RequestedSpeedEntry.Parse("12.5", vehicle);
        Assert.IsTrue(decimalEntry.IsAccepted);
        Assert.AreEqual(5.588, decimalEntry.MetersPerSecond, 1e-9);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("fast")]
    [DataRow("NaN")]
    [DataRow("-1")]
    [DataRow("-0.5")]
    public void RequestedSpeedEntry_RejectsNonNumericAndNegative(string text)
    {
        var entry = RequestedSpeedEntry.Parse(text, VehicleSpecification.Flexity2Blackpool);

        Assert.IsFalse(entry.IsAccepted);
        Assert.AreEqual(DisplayTone.Danger, entry.Message.Tone);
    }

    [TestMethod]
    public void RequestedSpeedEntry_AboveNominalMaximum_IsAcceptedWithWarning()
    {
        var entry = RequestedSpeedEntry.Parse("50", VehicleSpecification.Flexity2Blackpool);

        Assert.IsTrue(entry.IsAccepted, "The controller limits the target; the request itself is only a constraint.");
        Assert.AreEqual(DisplayTone.Warning, entry.Message.Tone);
        StringAssert.Contains(entry.Message.Text, "43.5 mph");
    }

    [TestMethod]
    public void TestFleetRow_ContainsOnlyTrainModelBoundaryFields()
    {
        var expected = new[] { "TrainId", "Active", "ActualSpeedInput", "PowerCommand", "ServiceBrakeCommand", "EmergencyBrakeCommand" };

        var actual = typeof(TestFleetRowStatus).GetProperties()
            .Where(p => p.DeclaringType == typeof(TestFleetRowStatus))
            .Select(p => p.Name).ToArray();

        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    public void TargetLimitedBy_IsShownAsText()
    {
        Assert.AreEqual("Authorized speed", TrainControllerPresenter.TargetConstraintText(TargetSpeedConstraint.AuthorizedSpeed).Text);
        Assert.AreEqual("Remaining authority", TrainControllerPresenter.TargetConstraintText(TargetSpeedConstraint.RemainingAuthority).Text);
        Assert.AreEqual(DisplayTone.Danger, TrainControllerPresenter.TargetConstraintText(TargetSpeedConstraint.EmergencyBrake).Tone);
        Assert.AreEqual("Driver service brake", TrainControllerPresenter.TargetConstraintText(TargetSpeedConstraint.DriverServiceBrake).Text);
    }

    [TestMethod]
    public void SmallNonZeroPower_IsNotShownAsZero()
    {
        Assert.AreEqual("0.013 hp", DisplayUnits.Power(10.0));
        Assert.AreEqual("0.0 hp", DisplayUnits.Power(0.0));
        Assert.AreEqual("1.3 hp", DisplayUnits.Power(1000.0));
    }

    [TestMethod]
    public void MainStatus_ExplainsTraction_ResetAvailability_AndClearedStation()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-001");
        slot.RecordTickResult(new TrainModelInput { IsActive = true, TrackSignalValid = true }, new TrainControllerOutput
        {
            TrainId = "TRAIN-001",
            Commands = new TrainModelCommand { EmergencyBrakeCommand = true },
            Display = new DriverDisplayState
            {
                TractionState = TractionState.EmergencyBrake,
                EmergencyBrakeLatched = true,
                EmergencyBrakeCauses = EmergencyBrakeCause.Passenger | EmergencyBrakeCause.AuthorityViolation,
                EmergencyBrakeResetBlockedReason = "passenger emergency brake request is still active",
                StationEvent = StationEvent.Departed,
            },
        });

        var status = TrainControllerPresenter.BuildMain(slot, null);

        Assert.AreEqual("0: emergency brake", status.PowerStatus.Text);
        StringAssert.Contains(status.EmergencyReset.Text, "Blocked");
        StringAssert.Contains(status.EmergencyCauses.Text, "Passenger request");
        StringAssert.Contains(status.EmergencyCauses.Text, "Cannot stop within authority");
        StringAssert.Contains(status.DistanceToStation.Text, "No upcoming station");
        StringAssert.Contains(status.StationGuidance.Text, "Departed");
        Assert.IsNull(status.Guidance.StationFeet, "No station icon on the guidance bar.");
    }
}
