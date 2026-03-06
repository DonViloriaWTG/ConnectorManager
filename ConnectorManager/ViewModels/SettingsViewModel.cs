using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectorManager.Models;
using ConnectorManager.Services;

namespace ConnectorManager.ViewModels;

/// <summary>
/// ViewModel for the Settings tab. Manages workspace paths and authentication configuration.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsPersistenceService _persistence = new();

    [ObservableProperty]
    private string _commonRepoPath = string.Empty;

    [ObservableProperty]
    private string _frameworkRepoPath = string.Empty;

    [ObservableProperty]
    private string _coreRepoPath = string.Empty;

    [ObservableProperty]
    private string _carrierConnectorRepoPath = string.Empty;

    [ObservableProperty]
    private string _devToolsRepoPath = string.Empty;

    [ObservableProperty]
    private string _apiBaseUrl = "http://localhost:5000";

    [ObservableProperty]
    private AuthMode _authenticationMode = AuthMode.ManualBearer;

    [ObservableProperty]
    private string _authorizationHeader = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public WorkspaceSettings ToSettings() => new()
    {
        CommonRepoPath = CommonRepoPath,
        FrameworkRepoPath = FrameworkRepoPath,
        CoreRepoPath = CoreRepoPath,
        CarrierConnectorRepoPath = CarrierConnectorRepoPath,
        DevToolsRepoPath = DevToolsRepoPath,
        ApiBaseUrl = ApiBaseUrl,
        AuthenticationMode = AuthenticationMode,
        AuthorizationHeader = AuthorizationHeader
    };

    public void LoadFromSettings(WorkspaceSettings settings)
    {
        CommonRepoPath = settings.CommonRepoPath;
        FrameworkRepoPath = settings.FrameworkRepoPath;
        CoreRepoPath = settings.CoreRepoPath;
        CarrierConnectorRepoPath = settings.CarrierConnectorRepoPath;
        DevToolsRepoPath = settings.DevToolsRepoPath;
        ApiBaseUrl = settings.ApiBaseUrl;
        AuthenticationMode = settings.AuthenticationMode;
        AuthorizationHeader = settings.AuthorizationHeader;
    }

    [RelayCommand]
    private void Save()
    {
        var settings = ToSettings();
        _persistence.Save(settings);
        StatusText = settings.IsValid
            ? "✔ Settings saved. All repo paths are valid."
            : "⚠ Settings saved. Some repo paths are missing or invalid.";

        SettingsSaved?.Invoke(settings);
    }

    [RelayCommand]
    private void AutoDetect()
    {
        if (string.IsNullOrEmpty(DevToolsRepoPath))
        {
            StatusText = "⚠ Set the DevTools repo path first, then auto-detect.";
            return;
        }

        var detected = WorkspaceSettings.AutoDetect(DevToolsRepoPath);
        LoadFromSettings(detected);
        StatusText = detected.IsValid
            ? "✔ All repos detected successfully."
            : "⚠ Some repos not found. Please set paths manually.";
    }

    public void Load()
    {
        var settings = _persistence.Load();
        LoadFromSettings(settings);
    }

    /// <summary>
    /// Raised when settings are saved, allowing other VMs to refresh.
    /// </summary>
    public event Action<WorkspaceSettings>? SettingsSaved;
}
