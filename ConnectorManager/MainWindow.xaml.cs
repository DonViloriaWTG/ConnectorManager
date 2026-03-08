using System.Reflection;
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

        // Set title with version from assembly (e.g. "1.0.3+75fd903")
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        Title = $"CMB Connector Manager v{version}";

        Closing += (_, _) => _viewModel.Dispose();
    }
}
