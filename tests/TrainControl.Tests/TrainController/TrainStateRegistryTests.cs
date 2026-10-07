using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.State;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class TrainStateRegistryTests
{
    [TestMethod]
    public void Registry_HasOneSlotPerFleetTrain_WithFixedControllerType()
    {
        var registry = new TrainStateRegistry();

        Assert.HasCount(10, registry.Trains);
        foreach (var trainId in TrainFleet.AllTrainIds)
        {
            var slot = registry.Get(trainId);
            Assert.AreEqual(trainId, slot.TrainId);
            Assert.AreEqual(TrainFleet.GetControllerType(trainId), slot.ControllerType);
        }

        Assert.ThrowsExactly<UnknownTrainException>(() => registry.Get("TRAIN-011"));
    }

    [TestMethod]
    public void DriverState_IsIndependentPerTrain()
    {
        var registry = new TrainStateRegistry();

        registry.Get("TRAIN-001").Driver.RequestedSpeedMetersPerSecond = 10.0;
        registry.Get("TRAIN-001").Driver.Mode = OperatingMode.Automatic;
        registry.Get("TRAIN-002").Driver.ServiceBrakeRequested = true;

        Assert.AreEqual(10.0, registry.Get("TRAIN-001").Driver.RequestedSpeedMetersPerSecond);
        Assert.AreEqual(OperatingMode.Automatic, registry.Get("TRAIN-001").Driver.Mode);
        Assert.IsFalse(registry.Get("TRAIN-001").Driver.ServiceBrakeRequested);

        Assert.AreEqual(0.0, registry.Get("TRAIN-002").Driver.RequestedSpeedMetersPerSecond);
        Assert.AreEqual(OperatingMode.Manual, registry.Get("TRAIN-002").Driver.Mode);
        Assert.IsTrue(registry.Get("TRAIN-002").Driver.ServiceBrakeRequested);

        foreach (var slot in registry.Trains.Skip(2))
        {
            Assert.AreEqual(new DriverInput(), slot.Driver.Peek());
        }
    }

    [TestMethod]
    public void EngineerSettings_DefaultToKp1Ki0_AndAreIndependentPerTrain()
    {
        var registry = new TrainStateRegistry();

        foreach (var slot in registry.Trains)
        {
            Assert.AreEqual(1.0, slot.Engineer.Kp);
            Assert.AreEqual(0.0, slot.Engineer.Ki);
        }

        registry.Get("TRAIN-004").SetEngineerSettings(2500.0, 12.5);

        Assert.AreEqual(2500.0, registry.Get("TRAIN-004").Engineer.Kp);
        Assert.AreEqual(12.5, registry.Get("TRAIN-004").Engineer.Ki);
        Assert.AreEqual(EngineerSettings.Default, registry.Get("TRAIN-003").Engineer);
        Assert.AreEqual(EngineerSettings.Default, registry.Get("TRAIN-005").Engineer);
    }

    [TestMethod]
    [DataRow(-0.1, 0.0)]
    [DataRow(0.0, -1.0)]
    [DataRow(double.NaN, 0.0)]
    [DataRow(0.0, double.PositiveInfinity)]
    public void EngineerSettings_RejectInvalidGains_AndKeepPreviousValues(double kp, double ki)
    {
        var slot = new TrainStateRegistry().Get("TRAIN-001");
        slot.SetEngineerSettings(3.0, 0.5);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => slot.SetEngineerSettings(kp, ki));

        Assert.AreEqual(3.0, slot.Engineer.Kp);
        Assert.AreEqual(0.5, slot.Engineer.Ki);
    }

    [TestMethod]
    public void EngineerSettings_StartupDefaults_AreFlaggedAsUntunedPlaceholder()
    {
        var registry = new TrainStateRegistry();
        var slot = registry.Get("TRAIN-002");

        Assert.IsTrue(slot.Engineer.IsUntunedStartupPlaceholder);

        slot.SetEngineerSettings(15_000.0, 300.0);
        Assert.IsFalse(slot.Engineer.IsUntunedStartupPlaceholder);

        registry.ResetAllRuntime();
        Assert.IsFalse(slot.Engineer.IsUntunedStartupPlaceholder, "Reset never restores the placeholder over tuned gains.");
    }

    [TestMethod]
    public void EngineerSettings_AcceptZeroAndLargeGains_NoInventedUpperBound()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-001");

        slot.SetEngineerSettings(0.0, 0.0);
        slot.SetEngineerSettings(1e7, 1e6);

        Assert.AreEqual(1e7, slot.Engineer.Kp);
    }

    [TestMethod]
    public void TestModelState_IsIndependentPerTrain_AndInactiveByDefault()
    {
        var registry = new TrainStateRegistry();

        foreach (var slot in registry.Trains)
        {
            Assert.IsFalse(slot.TestModel.IsActive, $"{slot.TrainId} must not run by default.");
        }

        var t6 = registry.Get("TRAIN-006").TestModel;
        t6.IsActive = true;
        t6.ActualSpeedMetersPerSecond = 12.0;
        t6.PassengerEmergencyBrakeRequested = true;

        var t7 = registry.Get("TRAIN-007").TestModel.PeekModelInput();
        Assert.IsFalse(t7.IsActive);
        Assert.AreEqual(0.0, t7.ActualSpeedMetersPerSecond);
        Assert.IsFalse(t7.PassengerEmergencyBrakeRequested);

        var t6Input = t6.PeekModelInput();
        Assert.IsTrue(t6Input.IsActive);
        Assert.AreEqual(12.0, t6Input.ActualSpeedMetersPerSecond);
        Assert.IsTrue(t6Input.PassengerEmergencyBrakeRequested);
    }

    [TestMethod]
    public void TestModelState_AcceptsEdgeCaseValues()
    {
        var model = new TrainStateRegistry().Get("TRAIN-001").TestModel;

        model.ActualSpeedMetersPerSecond = 30.0; // above the 19.44 m/s vehicle maximum
        model.RemainingAuthorityMeters = -5.0;   // invalid on purpose; validator handles it

        Assert.AreEqual(30.0, model.PeekModelInput().ActualSpeedMetersPerSecond);
        Assert.AreEqual(-5.0, model.PeekModelInput().RemainingAuthorityMeters);
    }

    [TestMethod]
    public void TransmitBeacon_IsReportedAsNewlyReceivedOnExactlyOneTick()
    {
        var model = new TrainStateRegistry().Get("TRAIN-002").TestModel;
        model.NextStationName = "PIONEER";
        model.DistanceToNextStationMeters = 250.0;
        model.PlatformSide = PlatformSide.Left;

        var beforeTransmit = model.TakeModelInputForTick().Beacon;
        Assert.IsFalse(beforeTransmit.IsValid, "Editing beacon fields alone is not a reception.");
        Assert.IsFalse(beforeTransmit.IsNewlyReceived);

        model.TransmitBeacon();
        Assert.IsTrue(model.PeekModelInput().Beacon.IsNewlyReceived, "Peek must not consume the reception.");

        var received = model.TakeModelInputForTick().Beacon;
        Assert.IsTrue(received.IsValid);
        Assert.IsTrue(received.IsNewlyReceived);
        Assert.AreEqual("PIONEER", received.NextStationName);
        Assert.AreEqual(250.0, received.DistanceToStationMeters);
        Assert.AreEqual(PlatformSide.Left, received.PlatformSide);

        var nextTick = model.TakeModelInputForTick().Beacon;
        Assert.IsTrue(nextTick.IsValid, "Beacon data stays valid after reception.");
        Assert.IsFalse(nextTick.IsNewlyReceived, "A reception is reported once.");

        model.DistanceToNextStationMeters = 100.0;
        model.TransmitBeacon();
        var second = model.TakeModelInputForTick().Beacon;
        Assert.IsTrue(second.IsNewlyReceived);
        Assert.AreEqual(100.0, second.DistanceToStationMeters);
    }

    [TestMethod]
    public void EmergencyBrakePresses_AreOneShot()
    {
        var driver = new TrainStateRegistry().Get("TRAIN-001").Driver;

        driver.PressEmergencyBrake();
        Assert.IsTrue(driver.Peek().EmergencyBrakeRequested);
        Assert.IsTrue(driver.Peek().EmergencyBrakeRequested, "Peek must not consume the press.");

        Assert.IsTrue(driver.TakeForTick().EmergencyBrakeRequested);
        Assert.IsFalse(driver.TakeForTick().EmergencyBrakeRequested);

        driver.PressEmergencyBrakeReset();
        Assert.IsTrue(driver.TakeForTick().EmergencyBrakeResetRequested);
        Assert.IsFalse(driver.TakeForTick().EmergencyBrakeResetRequested);
    }

    [TestMethod]
    public void DriverRequestedSpeed_RejectsNegativeAndNonFinite()
    {
        var driver = new TrainStateRegistry().Get("TRAIN-001").Driver;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => driver.RequestedSpeedMetersPerSecond = -1.0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => driver.RequestedSpeedMetersPerSecond = double.NaN);
        Assert.AreEqual(0.0, driver.RequestedSpeedMetersPerSecond);
    }

    [TestMethod]
    public void ResetAllRuntime_ClearsOutputs_KeepsEngineerDriverAndTestInputs()
    {
        var registry = new TrainStateRegistry();
        var slot = registry.Get("TRAIN-003");

        slot.SetEngineerSettings(4.0, 0.25);
        slot.Driver.RequestedSpeedMetersPerSecond = 8.0;
        slot.Driver.PressEmergencyBrake();
        slot.TestModel.IsActive = true;
        slot.TestModel.ActualSpeedMetersPerSecond = 5.0;
        slot.TestModel.TransmitBeacon();
        Assert.IsTrue(slot.TestModel.BeaconReceptionPending, "Precondition: a reception is pending before Reset.");
        slot.RecordTickResult(slot.TestModel.PeekModelInput(), new TrainControllerOutput { TrainId = "TRAIN-003", TickId = 7 });

        registry.ResetAllRuntime();

        Assert.IsNull(slot.LastOutput);
        Assert.IsNull(slot.LastModelInput);
        Assert.IsFalse(slot.Driver.EmergencyBrakePressPending);
        Assert.IsFalse(slot.TestModel.BeaconReceptionPending);
        Assert.IsTrue(slot.TestModel.BeaconValid, "Entered beacon values are kept.");
        Assert.AreEqual(4.0, slot.Engineer.Kp);
        Assert.AreEqual(0.25, slot.Engineer.Ki);
        Assert.AreEqual(8.0, slot.Driver.RequestedSpeedMetersPerSecond);
        Assert.IsTrue(slot.TestModel.IsActive);
        Assert.AreEqual(5.0, slot.TestModel.ActualSpeedMetersPerSecond);
    }

    [TestMethod]
    public void RecordTickResult_RejectsAnotherTrainsOutput()
    {
        var slot = new TrainStateRegistry().Get("TRAIN-001");

        Assert.ThrowsExactly<ArgumentException>(() =>
            slot.RecordTickResult(new TrainModelInput(), new TrainControllerOutput { TrainId = "TRAIN-002" }));
        Assert.IsNull(slot.LastOutput);
    }

    [TestMethod]
    public void StartupMode_DefaultsToManual_ProvisionalAndConfigurable()
    {
        Assert.AreEqual(OperatingMode.Manual, ControllerStartupDefaults.Default.InitialOperatingMode);

        foreach (var slot in new TrainStateRegistry().Trains)
        {
            Assert.AreEqual(OperatingMode.Manual, slot.Driver.Mode);
        }

        var configured = new TrainStateRegistry(new ControllerStartupDefaults
        {
            InitialOperatingMode = OperatingMode.Automatic,
            InitialCabinTemperatureSetpointCelsius = 19.0,
            InitialTestCabinTemperatureCelsius = 25.0,
        });

        foreach (var slot in configured.Trains)
        {
            Assert.AreEqual(OperatingMode.Automatic, slot.Driver.Mode);
            Assert.AreEqual(19.0, slot.Driver.CabinTemperatureSetpointCelsius);
            Assert.AreEqual(25.0, slot.TestModel.CabinTemperatureCelsius);
        }
    }

    [TestMethod]
    public void TrainControllerState_HasNoDispatchOrRunningFlag()
    {
        // Running/active state must come from the model input (Train Model or Test Model state),
        // never from a Train Controller-owned toggle.
        string[] forbidden = { "IsRunning", "IsActive", "IsDispatched" };

        foreach (var type in new[] { typeof(TrainRuntimeSlot), typeof(TrainStateRegistry), typeof(DriverInputState) })
        {
            foreach (var name in forbidden)
            {
                Assert.IsNull(type.GetProperty(name), $"{type.Name} must not own '{name}'.");
            }
        }
    }

    [TestMethod]
    public void AuthorityChange_IsReportedAsUpdateOnExactlyOneTick()
    {
        var model = new TrainStateRegistry().Get("TRAIN-001").TestModel;

        Assert.IsFalse(model.TakeModelInputForTick().AuthorityUpdateReceived);

        model.RemainingAuthorityMeters = 300.0;
        Assert.IsTrue(model.AuthorityUpdatePending);
        Assert.IsTrue(model.TakeModelInputForTick().AuthorityUpdateReceived);
        Assert.IsFalse(model.TakeModelInputForTick().AuthorityUpdateReceived);

        model.RemainingAuthorityMeters = 300.0; // same value: not a new update
        Assert.IsFalse(model.AuthorityUpdatePending);

        model.SendAuthorityUpdate();             // explicit re-send of the same value
        Assert.IsTrue(model.TakeModelInputForTick().AuthorityUpdateReceived);

        model.RemainingAuthorityMeters = 200.0;
        model.ClearPendingEvents();
        Assert.IsFalse(model.AuthorityUpdatePending);
        Assert.AreEqual(200.0, model.RemainingAuthorityMeters, "Reset keeps the entered value.");
    }

    [TestMethod]
    public void DriverAnnouncement_IsOneShot_BlankIgnored_ClearedByReset()
    {
        var driver = new TrainStateRegistry().Get("TRAIN-001").Driver;

        driver.RequestAnnouncement("   ");
        Assert.AreEqual(string.Empty, driver.AnnouncementPending);

        driver.RequestAnnouncement(" Doors closing. ");
        Assert.AreEqual("Doors closing.", driver.Peek().AnnouncementRequest, "Peek does not consume.");
        Assert.AreEqual("Doors closing.", driver.TakeForTick().AnnouncementRequest);
        Assert.AreEqual(string.Empty, driver.TakeForTick().AnnouncementRequest);

        driver.RequestAnnouncement("Delay ahead.");
        driver.ClearPendingPresses();
        Assert.AreEqual(string.Empty, driver.AnnouncementPending);
    }

    [TestMethod]
    public void LastAnnouncement_PersistsAfterItsTick_ClearedByReset()
    {
        var registry = new TrainStateRegistry();
        var slot = registry.Get("TRAIN-001");

        slot.RecordTickResult(new TrainModelInput(), new TrainControllerOutput
        {
            TrainId = "TRAIN-001",
            TickId = 5,
            Commands = new TrainModelCommand { StationAnnouncement = "Arrived at PIONEER." },
        });
        slot.RecordTickResult(new TrainModelInput(), new TrainControllerOutput { TrainId = "TRAIN-001", TickId = 6 });

        Assert.AreEqual("Arrived at PIONEER.", slot.LastAnnouncement);
        Assert.AreEqual(5L, slot.LastAnnouncementTick);

        registry.ResetAllRuntime();
        Assert.AreEqual(string.Empty, slot.LastAnnouncement);
    }
}
