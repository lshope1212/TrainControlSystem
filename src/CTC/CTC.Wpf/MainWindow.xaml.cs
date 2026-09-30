using System.Windows;
using CTC.Core.Interfaces;
using CTC.Core.Services;
using CTC.Wpf.Services;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind only composes dependencies;
/// all behavior lives in the view model and CTC.Core.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        IMessageSender sender = new NamedPipeMessageSender();
        ICTCService ctcService = new CTCService(sender);
        DataContext = new MainWindowViewModel(ctcService);
    }
}
