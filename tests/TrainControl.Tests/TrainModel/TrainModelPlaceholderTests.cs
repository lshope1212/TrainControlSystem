using TrainModel.Core.Models;
using TrainModel.Core.Services;

namespace TrainControl.Tests.TrainModel;

[TestClass]
public class TrainModelPlaceholderTests
{
    [TestMethod]
    public void TrainSimulationService_ExposesATrain()
    {
        var service = new TrainSimulationService();

        Assert.IsNotNull(service.Train);
    }

    [TestMethod]
    public void Train_DefaultsToZeroPosition()
    {
        var train = new Train();

        Assert.AreEqual(0.0, train.PositionMeters);
    }
}
