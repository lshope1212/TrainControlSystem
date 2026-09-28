using TrainControl.Contracts.Messages;
using TrainController.Core.Services;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class TrainControllerPlaceholderTests
{
    [TestMethod]
    public void TrainControllerService_AcceptsSharedContractMessages()
    {
        var service = new TrainControllerService();

        service.ApplyTrainState(new TrainStateMessage { TrainId = "TRAIN-001" });
        service.ApplyAuthority(new AuthorityMessage { TrainId = "TRAIN-001", AuthorityMeters = 100 });

        Assert.AreEqual("TRAIN-001", service.GetSpeedCommand().TrainId);
    }
}
