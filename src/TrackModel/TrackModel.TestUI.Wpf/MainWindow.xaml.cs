using System.Windows;
using TrackModel.TestUI.Wpf.ViewModels;
namespace TrackModel.TestUI.Wpf;
public partial class MainWindow : Window
{
    private Window? _messagesWindow;
    public MainWindow(MainWindowViewModel vm) { InitializeComponent(); DataContext = vm; }

    private void ShowMessages(object sender, RoutedEventArgs e)
    {
        if (_messagesWindow is not null) { _messagesWindow.Activate(); return; }
        _messagesWindow = new MessageLogWindow { Owner = this, DataContext = DataContext };
        _messagesWindow.Closed += (_, _) => _messagesWindow = null;
        _messagesWindow.Show();
    }
}
