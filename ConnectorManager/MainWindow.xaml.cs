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

        // Set title with version from assembly (e.g. "1.0.42+fdb56ce")
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";

        // Trim commit hash to 7 characters
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0 && version.Length > plusIndex + 8)
            version = version[..(plusIndex + 8)];

        Title = $"CMB Connector Manager v{version}";

        Closing += (_, _) => _viewModel.Dispose();
    }
}
