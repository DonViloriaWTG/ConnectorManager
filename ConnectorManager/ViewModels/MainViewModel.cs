using CommunityToolkit.Mvvm.ComponentModel;
using ConnectorManager.Models;

namespace ConnectorManager.ViewModels;

/// <summary>
/// Root ViewModel for the MainWindow. Hosts all tab ViewModels and orchestrates navigation.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public BuildChainViewModel BuildChain { get; } = new();
    public ApiManagerViewModel ApiManager { get; } = new();
    public DeployConnectorViewModel DeployConnector { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainViewModel()
    {
        Settings.SettingsSaved += OnSettingsSaved;

        // When repo paths change in the Build Chain tab, sync back to Settings,
        // re-initialize the API Manager so it uses the updated Core path,
        // and persist so the app remembers them on next launch.
        BuildChain.RepoPathsChanged += (common, framework, core) =>
        {
            Settings.CommonRepoPath = common;
            Settings.FrameworkRepoPath = framework;
            Settings.CoreRepoPath = core;
            Settings.SaveQuietly();

            ApiManager.Initialize(Settings.ToSettings());
        };

        // When the CarrierConnector repo path changes in the Deploy tab, sync back to Settings
        DeployConnector.CarrierConnectorRepoPathChanged += path =>
        {
            Settings.CarrierConnectorRepoPath = path;
            Settings.SaveQuietly();
        };

        // When the Core repo path changes in the API Manager tab, sync back to Settings
        ApiManager.CoreRepoPathChanged += path =>
        {
            Settings.CoreRepoPath = path;
            Settings.SaveQuietly();
        };

        // When the Deploy tab requests to debug a connector, restart the API with debug args
        // and wait for it to be ready, returning the PID for debugger attachment
        DeployConnector.DebugConnectorRequested += async (debugPackagePath, debugPackageName) =>
        {
            await ApiManager.StartWithDebugAsync(debugPackagePath, debugPackageName).ConfigureAwait(true);
            return await ApiManager.WaitForReadyAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(true);
        };

        // When the API detects its actual listening URL, propagate it
        // to the Deploy Connector tab so uploads go to the right port.
        ApiManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ApiManagerViewModel.ApiUrl) &&
                ApiManager.ApiUrl is not null &&
                !ApiManager.ApiUrl.StartsWith("(", StringComparison.Ordinal))
            {
                DeployConnector.UpdateApiUrl(ApiManager.ApiUrl);
            }
        };
    }

    /// <summary>
    /// Initializes the application: loads settings and propagates to child VMs.
    /// </summary>
    public void Initialize()
    {
        Settings.Load();
        var currentSettings = Settings.ToSettings();

        // Persist auto-detected settings so subsequent launches remember them
        // instead of re-running auto-detection (which may pick up wrong paths).
        Settings.SaveQuietly();

        PropagateSettings(currentSettings);
    }

    private void OnSettingsSaved(WorkspaceSettings settings)
    {
        PropagateSettings(settings);
    }

    private void PropagateSettings(WorkspaceSettings settings)
    {
        BuildChain.Initialize(settings);
        ApiManager.Initialize(settings);
        DeployConnector.Initialize(settings);
    }

    public void Dispose()
    {
        ApiManager.Dispose();
    }
}
