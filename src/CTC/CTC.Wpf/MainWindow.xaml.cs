using System.Windows;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Dependencies are composed in <see cref="App"/>;
/// all behavior lives in the view model and CTC.Core.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
