using System.Windows;
using CTC.Core.Interfaces;
using CTC.Core.Services;
using CTC.TestUI.Wpf.Services;
using CTC.TestUI.Wpf.ViewModels;

namespace CTC.TestUI.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind only composes dependencies:
/// the real CTCService wired to a RecordingMessageSender instead of a named pipe.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var sender = new RecordingMessageSender();
        ICTCService ctcService = new CTCService(sender);
        DataContext = new MainWindowViewModel(ctcService, sender);
    }
}
