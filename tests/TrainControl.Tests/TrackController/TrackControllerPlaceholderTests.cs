using TrackController.Core.Services;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackController;

[TestClass]
public class TrackControllerPlaceholderTests
{
    [TestMethod]
    public void TrackControllerService_AcceptsSharedContractMessages()
    {
        var service = new TrackControllerService();

        service.ApplyTrackState(new TrackStateMessage { BlockId = "A1" });

        Assert.AreEqual("A1", service.GetTrackState("A1").BlockId);
    }
}
