using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrackController.Tests;
using static TrackController.Wpf.Tests.UiTestSession;

namespace TrackController.Wpf.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OperatorViewTests
{
    [TestMethod]
    public Task LineAndTerritorySelection_ShowsOnlyOwnedBlocksAndResetsBlockSelection() => RunAsync(async session =>
    {
        using var files = new DeploymentFiles();
        await session.Controller.LoadDeploymentAsync(files.PathFor("course-territories-all-pc.deployment.json"));
        var vm = session.Controller;
        Assert.HasCount(3, vm.MapLines);
        var seen = new HashSet<string>();
        foreach (string line in vm.MapLines)
        {
            vm.SelectedMapLine = line;
            Assert.HasCount(2, vm.MapTerritories);
            foreach (var territory in vm.MapTerritories)
            {
                vm.SelectedMapTerritory = territory;
                Assert.IsNotEmpty(vm.MapBlocks);
                Assert.Contains(vm.SelectedBlockId, territory.Blocks);
                CollectionAssert.AreEquivalent(territory.Blocks.ToArray(), vm.MapBlocks.Select(block => block.BlockId).ToArray());
                Assert.IsTrue(vm.MapBlocks.All(block => seen.Add(block.BlockId)), "A block must be displayed in precisely one territory.");
            }
        }
        Assert.HasCount(241, seen);
    });

    [TestMethod]
    public Task OperatorViews_RenderOffscreenAtLaptopSize() => RunAsync(async session =>
    {
        using var files = new DeploymentFiles();
        await session.Controller.LoadDeploymentAsync(files.PathFor("course-territories-all-pc.deployment.json"));
        session.Controller.SelectedMapLine = "Green Line";
        var window = new TrackController.Wpf.MainWindow(session.Controller);
        Assert.IsLessThanOrEqualTo(SystemParameters.WorkArea.Width, window.Width);
        Assert.IsLessThanOrEqualTo(SystemParameters.WorkArea.Height, window.Height);
        Assert.AreEqual(SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - window.Width) / 2, window.Left, 0.01);
        var view = (FrameworkElement)window.Content;
        for (int tab = 0; tab < 4; tab++)
        {
            session.Controller.TabIndex = tab;
            view.Measure(new Size(1180, 650));
            view.Arrange(new Rect(0, 0, 1180, 650));
            view.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            Assert.IsTrue(view.IsArrangeValid);
            if (Environment.GetEnvironmentVariable("TRACK_CONTROLLER_RENDER_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(1180, 650, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, $"operator-tab-{tab}.png"));
                encoder.Save(stream);
            }
        }
        // Never show the window: no focus or desktop interaction is needed for this check.
        window.Content = null;
        window.Close();
    });
}
