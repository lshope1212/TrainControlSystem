using System.Windows;
using TrainModel.TestUI.Wpf.ViewModels;

namespace TrainModel.TestUI.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
