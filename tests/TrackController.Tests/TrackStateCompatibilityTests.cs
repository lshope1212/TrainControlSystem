using TrackController.Core.Services;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>Exercises shared track-state message compatibility without changing live controller state.</summary>
[TestClass]
public class TrackStateCompatibilityTests
{
    /// <summary>Verifies that track controller service accepts shared contract messages.</summary>
    [TestMethod]
    public void ApplyTrackState_SharedMessage_UpdatesReportedState()
    {
        var service = new TrackControllerService();
        service.ApplyTrackState(new TrackStateMessage { BlockId = "1" });
        Assert.AreEqual("1", service.GetTrackState("1").BlockId);
    }
}
