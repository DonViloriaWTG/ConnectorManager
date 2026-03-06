using System.Windows;
using ConnectorManager.ViewModels;

namespace ConnectorManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Initialize();

        Closing += (_, _) => _viewModel.Dispose();
    }
}
