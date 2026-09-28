using TrackModel.Core.Models;
using TrackModel.Core.Services;

namespace TrainControl.Tests.TrackModel;

[TestClass]
public class TrackModelPlaceholderTests
{
    [TestMethod]
    public void TrackService_StartsWithAnEmptyLayout()
    {
        var service = new TrackService();

        Assert.IsEmpty(service.Layout.Blocks);
    }

    [TestMethod]
    public void TrackBlock_DefaultsToUnoccupied()
    {
        var block = new TrackBlock();

        Assert.IsFalse(block.IsOccupied);
    }
}
