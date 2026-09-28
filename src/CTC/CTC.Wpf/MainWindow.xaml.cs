using System.Windows;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind is intentionally kept empty.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
