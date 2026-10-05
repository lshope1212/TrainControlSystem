using System.Windows;
using TrackModel.Wpf.ViewModels;
namespace TrackModel.Wpf;
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
