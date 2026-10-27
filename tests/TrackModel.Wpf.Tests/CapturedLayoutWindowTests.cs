using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TrackModel.Core.Services;
using TrackModel.TestUI.Wpf;
using TrackModel.TestUI.Wpf.ViewModels;

namespace TrackModel.Wpf.Tests;

[STATestClass]
public class CapturedLayoutWindowTests
{
    [TestMethod]
    public void EquipmentTab_RendersReadOnlyCapturedFlagsWithoutCrashing()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackModel.TestUI.Wpf;component/Resources/Theme.xaml", UriKind.Relative)
        });
        var service = new TrackService();
        BlueLineTrackLayout.LoadDemonstration(service);
        var blocks = new ObservableCollection<CapturedBlockViewModel>();
        foreach (var definition in service.CreateLayoutMessage().Lines.SelectMany(line => line.Blocks))
        {
            var block = new CapturedBlockViewModel(definition.BlockId);
            block.ApplyDefinition(definition);
            blocks.Add(block);
        }

        var window = new LayoutWindow { DataContext = new { OutputBlocks = blocks }, ShowActivated = false };
        Exception? bindingException = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { bindingException = e.Exception; e.Handled = true; };
        window.Dispatcher.UnhandledException += handler;
        try
        {
            window.Show();
            var tabs = (TabControl)((DockPanel)window.Content).Children[1];
            tabs.SelectedIndex = 1;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.IsNull(bindingException, bindingException?.ToString());
            var grid = (DataGrid)((TabItem)tabs.Items[1]).Content;
            Assert.IsTrue(grid.IsReadOnly);
            Assert.HasCount(15, grid.Items);
            foreach (var column in grid.Columns.OfType<DataGridCheckBoxColumn>())
                Assert.AreEqual(BindingMode.OneWay, ((Binding)column.Binding).Mode);
            Assert.IsNotNull(grid.ItemContainerGenerator.ContainerFromIndex(4));
        }
        finally
        {
            window.Close();
            window.Dispatcher.UnhandledException -= handler;
        }
    }
}
