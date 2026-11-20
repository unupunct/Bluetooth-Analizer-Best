using System.Windows;
using BluetoothFleetMonitor.Desktop.ViewModels;

namespace BluetoothFleetMonitor.Desktop.Shell;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
