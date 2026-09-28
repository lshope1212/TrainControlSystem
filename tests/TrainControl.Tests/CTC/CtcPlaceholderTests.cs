using CTC.Core.Services;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcPlaceholderTests
{
    [TestMethod]
    public void CtcService_StartsWithNoTrainsTracked()
    {
        var service = new CTCService();

        Assert.IsEmpty(service.SystemState.Trains);
    }

    [TestMethod]
    public void CtcService_StartsInManualMode()
    {
        var service = new CTCService();

        Assert.IsFalse(service.DispatcherState.IsAutomaticMode);
    }
}
