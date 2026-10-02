using System.Windows;
using CTC.TestUI.Wpf.ViewModels;

namespace CTC.TestUI.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Dependencies are composed in <see cref="App"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
