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
            Assert.HasCount(line == "Blue Line" ? 2 : line == "Red Line" ? 6 : 8, vm.MapTerritories);
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
        // Exercise the actual setup bindings: editing one territory must survive selecting another.
        await SetMaintenanceAsync(session, true);
        var selected = session.Controller.SelectedPlcTerritory!;
        var target = (System.Windows.Controls.ComboBox)window.FindName("SetupTarget");
        var endpoint = (FrameworkElement)window.FindName("SetupPiEndpoint");
        Assert.AreEqual(Visibility.Collapsed, endpoint.Visibility);
        target.SelectedItem = "Pi";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(session.Controller.CanApplyExecution);
        Assert.AreEqual(Visibility.Visible, endpoint.Visibility);
        await RenderSetup("setup-pi-draft", 1180, 650);
        var filter = (System.Windows.Controls.ComboBox)window.FindName("SetupLineFilter");
        var territories = (System.Windows.Controls.ListBox)window.FindName("SetupTerritories");
        filter.SelectedItem = "Red Line";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.HasCount(6, territories.Items);
        Assert.AreEqual("Red Line", session.Controller.SelectedPlcTerritory?.Line);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(Visibility.Collapsed, endpoint.Visibility);
        Assert.IsTrue(selected.HasChanges, "Selecting another territory must retain unapplied edits.");
        selected.Target = "PC";
        Assert.IsFalse(session.Controller.HasExecutionChanges);
        ((System.Windows.Controls.Expander)window.FindName("SetupAdvanced")).IsExpanded = true;
        filter.SelectedItem = "Green Line";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.HasCount(8, territories.Items);
        Assert.AreEqual("Green Line", session.Controller.SelectedPlcTerritory?.Line);
        var plcStatus = (System.Windows.Controls.TextBlock)window.FindName("SetupPlcStatus");
        Assert.AreEqual("Program loaded. Automatic evaluation active.", plcStatus.Text);
        await RenderSetup("setup-advanced", 1180, 650);
        ((System.Windows.Controls.Expander)window.FindName("SetupAdvanced")).IsExpanded = false;
        await RenderSetup("setup-small", 800, 540);

        async Task RenderSetup(string name, int width, int height)
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var apply = (FrameworkElement)window.FindName("SetupApply");
            var bounds = apply.TransformToAncestor(view).TransformBounds(new Rect(apply.RenderSize));
            Assert.IsTrue(bounds.Bottom <= height && bounds.Right <= width, "Apply must remain accessible at small window sizes.");
            if (Environment.GetEnvironmentVariable("TRACK_CONTROLLER_RENDER_DIR") is { Length: > 0 } output)
            {
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, name + ".png"));
                encoder.Save(stream);
            }
        }

        session.Controller.TabIndex = 0;
        foreach (string line in session.Controller.MapLines)
        {
            session.Controller.SelectedMapLine = line;
            foreach (var territory in session.Controller.MapTerritories)
            {
                session.Controller.SelectedMapTerritory = territory;
                view.Measure(new Size(1180, 650));
                view.Arrange(new Rect(0, 0, 1180, 650));
                view.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                view.UpdateLayout();
                StringAssert.StartsWith(session.Controller.MapHandoffs, "Handoffs:", territory.Name);
                var map = (FrameworkElement)window.FindName("TrackMap");
                var tracks = VisualTreeHelper.GetDrawing(map).Children.OfType<GeometryDrawing>()
                    .Where(drawing => drawing.Geometry is StreamGeometry && drawing.Brush is null)
                    .Select(drawing => drawing.Geometry).Distinct().ToArray();
                Assert.HasCount(territory.Blocks.Count, tracks, $"Each owned block should have a track segment in {territory.Name}.");
                if (Environment.GetEnvironmentVariable("TRACK_CONTROLLER_RENDER_DIR") is { Length: > 0 } output)
                {
                    var bitmap = new RenderTargetBitmap(1180, 650, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, territory.Id + ".png"));
                    encoder.Save(stream);
                }
            }
        }
        // Never show the window: no focus or desktop interaction is needed for this check.
        window.Content = null;
        window.Close();
    });
}
